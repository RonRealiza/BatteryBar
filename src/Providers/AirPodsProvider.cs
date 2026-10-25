using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryBar.Providers;

/// <summary>
/// AirPods и Beats. Windows их заряд не знает, но наушники постоянно рассылают Bluetooth-рекламу Apple
/// (Proximity Pairing, тип 0x07) с зарядом каждого наушника, футляра и признаками зарядки — так работают
/// OpenPods и MagicPods. Ничего не отправляем, только слушаем эфир.
/// </summary>
/// <remarks>
/// В рекламе нет постоянного адреса, а рядом могут быть чужие AirPods. Поэтому показываем заряд,
/// только пока ваши AirPods/Beats подключены к компьютеру, и берём самый сильный (ближайший) сигнал.
/// </remarks>
internal sealed class AirPodsProvider : IBatteryProvider, IDisposable
{
    private const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairing = 0x07;
    private const short MinRssi = -75;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(20);

    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Passive };
    private readonly object _sync = new();
    private readonly List<(DateTime Time, short Rssi, byte[] Data)> _recent = new();

    public AirPodsProvider()
    {
        _watcher.Received += OnReceived;
    }

    public string Name => "airpods";

    public TimeSpan PollInterval => TimeSpan.FromSeconds(10);

    public IEnumerable<DeviceBattery> Poll()
    {
        if (ConnectedAppleAudio() is not { } name) return Array.Empty<DeviceBattery>();

        // Слушаем эфир только пока AirPods подключены — так меньше лишней работы радио.
        if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
        {
            try { _watcher.Start(); }
            catch (Exception ex) { Log.Error("airpods watcher", ex); }
        }

        byte[]? data;
        lock (_sync)
        {
            _recent.RemoveAll(r => DateTime.UtcNow - r.Time > Fresh);
            data = _recent.Where(r => r.Rssi >= MinRssi).OrderByDescending(r => r.Rssi).Select(r => r.Data).FirstOrDefault();
        }
        if (data == null || Decode(data) is not { } pods) return Array.Empty<DeviceBattery>();

        int? percent = new[] { pods.Left, pods.Right }.Where(v => v != null).Min();
        if (percent == null) return Array.Empty<DeviceBattery>();

        string Part(string label, int? v) => v is { } p ? $"{label} {p}%" : "";
        string details = string.Join(", ", new[] { Part("L", pods.Left), Part("R", pods.Right), Part("футляр", pods.Case) }
            .Where(s => s.Length > 0));
        var state = pods.Charging ? ChargeState.Charging : ChargeState.Discharging;
        return new[] { new DeviceBattery($"airpods:{name}", $"{name} ({details})", DeviceKind.Headset, percent, state) };
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var md in args.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AppleCompanyId || md.Data.Length < 9) continue;
            var bytes = new byte[md.Data.Length];
            using (var reader = DataReader.FromBuffer(md.Data)) reader.ReadBytes(bytes);
            if (bytes[0] != ProximityPairing) continue;
            lock (_sync)
            {
                _recent.Add((DateTime.UtcNow, args.RawSignalStrengthInDBm, bytes));
                if (_recent.Count > 200) _recent.RemoveRange(0, _recent.Count - 200);
            }
        }
    }

    /// <summary>
    /// Разбор рекламы (данные без кода компании): [5] бит 0x20 — какой наушник основной (если 0, левый и правый
    /// в [6] меняются местами); [6] — заряд наушников по полубайтам; [7] — старший полубайт флаги зарядки
    /// (наушники, 0x4 — футляр), младший — заряд футляра. Значения 0–10 = десятки процентов, 15 — неизвестно.
    /// </summary>
    private static (int? Left, int? Right, int? Case, bool Charging)? Decode(byte[] d)
    {
        bool flip = (d[5] & 0x20) == 0;
        int hi = d[6] >> 4, lo = d[6] & 0x0F;
        int left = flip ? hi : lo, right = flip ? lo : hi;
        int flags = d[7] >> 4;
        bool chargeL = (flags & (flip ? 0x02 : 0x01)) != 0;
        bool chargeR = (flags & (flip ? 0x01 : 0x02)) != 0;

        static int? Percent(int v) => v <= 10 ? v * 10 : null;
        int? l = Percent(left), r = Percent(right);
        if (l == null && r == null) return null;

        // «Заряжаются» — если заряжаются все наушники, о которых что-то известно.
        bool charging = (l == null || chargeL) && (r == null || chargeR);
        return (l, r, Percent(d[7] & 0x0F), charging);
    }

    /// <summary>Имя подключённых сейчас AirPods/Beats (из сопряжённых Bluetooth-устройств) или null.</summary>
    private static string? ConnectedAppleAudio()
    {
        var endpoints = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                new[] { "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint)
            .AsTask().GetAwaiter().GetResult();
        return endpoints
            .Where(e => e.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true)
            .Select(e => e.Name)
            .FirstOrDefault(n => n.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
                                 || n.Contains("Beats", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _watcher.Received -= OnReceived;
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
    }
}
