using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Клавиатура Zornher ZH870 через 2.4 ГГц ресивер (выдаёт себя за 05AC:024F).
/// Протокол подсмотрен у фирменного драйвера: интерфейс MI_03, отчёт 0x00 на 33 байта,
/// байт 1 — команда, байт 32 — сумма байтов 1..31. Команда 0x20 возвращает заряд в байте 4.
/// </summary>
internal sealed class ZornherProvider : IBatteryProvider
{
    private const int VendorId = 0x05AC;
    private const int ProductId = 0x024F;
    private const int PacketLength = 33;
    private const byte CmdBattery = 0x20;

    // Заряд считается по напряжению и прыгает на ±1% — показываем медиану последних замеров.
    private const int SmoothWindow = 5;
    private readonly Queue<int> _recent = new();
    private string? _lastRaw;

    // Отдельного признака зарядки клавиатура не шлёт. Но кабель поднимает напряжение, и процент за один опрос
    // подскакивает на 10–15 (до ~70%), а при отключении так же резко падает. Ловим эти скачки:
    // на время зарядки показываем последний честный процент, после отключения ждём, пока напряжение осядет.
    // Похожий скачок на 5–10% бывает и при пробуждении клавиатуры, но он за 1–2 опроса оседает,
    // а от кабеля процент продолжает расти. Поэтому зарядку признаём, только если скачок держится ConfirmPolls опросов.
    private const int JumpThreshold = 5;
    private const int ConfirmPolls = 3;
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(60);
    private int? _prevRaw;
    private bool _charging;
    private int? _frozen;
    private DateTime _settleUntil;
    private int _candidatePolls;
    private int _candidateBase;

    public string Name => "zornher";

    private (int Percent, ChargeState State) Estimate(int raw)
    {
        var now = DateTime.UtcNow;
        int? prev = _prevRaw;
        _prevRaw = raw;

        if (!_charging && _candidatePolls > 0)
        {
            // Проверяем подозрение на кабель: процент должен оставаться высоким и не оседать.
            if (raw >= _candidateBase + JumpThreshold && prev is { } last && raw >= last - 1)
            {
                if (++_candidatePolls >= ConfirmPolls)
                {
                    _charging = true;
                    _candidatePolls = 0;
                    _frozen ??= _candidateBase;
                    Log.Write($"zh870: cable plugged (from {_candidateBase}% to {raw}%), showing {_frozen}%");
                }
                else
                {
                    return (_candidateBase, ChargeState.Discharging);
                }
            }
            else
            {
                Log.Write($"zh870: spike ignored ({_candidateBase}% -> {raw}%)");
                _candidatePolls = 0;
            }
        }
        else if (!_charging && prev is { } p && raw - p >= JumpThreshold)
        {
            _candidatePolls = 1;
            _candidateBase = Median() ?? p;
            return (_candidateBase, ChargeState.Discharging);
        }
        else if (_charging && prev is { } q && q - raw >= JumpThreshold)
        {
            _charging = false;
            _settleUntil = now + SettleTime;
            _recent.Clear();
            Log.Write($"zh870: cable unplugged ({q}% -> {raw}%)");
        }

        if (_charging) return (_frozen ?? raw, ChargeState.Charging);
        if (now < _settleUntil && _frozen is { } f) return (f, ChargeState.Discharging);

        _frozen = null;
        _recent.Enqueue(raw);
        while (_recent.Count > SmoothWindow) _recent.Dequeue();
        return (Median()!.Value, ChargeState.Discharging);
    }

    private int? Median()
    {
        if (_recent.Count == 0) return null;
        var sorted = _recent.OrderBy(p => p).ToList();
        return sorted[sorted.Count / 2];
    }

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId, ProductId).Where(IsVendorInterface))
        {
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    var r = Request(stream, CmdBattery);
                    string raw = r == null ? "no reply" : string.Join(" ", r.Take(8).Select(b => b.ToString("X2")));
                    if (raw != _lastRaw)
                    {
                        Log.Write($"zh870 raw: {raw}");
                        _lastRaw = raw;
                    }
                    int percent = r?[4] ?? 0;
                    // Нет ответа или 0 — скорее всего, клавиатура выключена: ресивер отвечает за неё сам.
                    if (percent is 0 or > 100)
                    {
                        _prevRaw = null;
                        continue;
                    }
                    var (shown, state) = Estimate(percent);
                    result.Add(new DeviceBattery("zh870", "ZH870", DeviceKind.Keyboard, shown, state));
                }
            }
            catch (Exception ex)
            {
                Log.Error("zornher", ex);
            }
        }
        return result;
    }

    /// <summary>
    /// Код 05AC:024F выдают многие китайские клавиатуры, и чужой модели наша команда 0x20 может значить что угодно.
    /// Поэтому шлём её только устройству, которое само называет себя ZH870.
    /// </summary>
    private static bool IsVendorInterface(HidDevice d)
    {
        if (!d.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return d.GetMaxOutputReportLength() == PacketLength && d.GetMaxInputReportLength() == PacketLength
                && d.GetProductName().Contains("ZH870", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? Request(HidStream stream, byte command)
    {
        var tx = new byte[PacketLength];
        tx[1] = command;
        tx[PacketLength - 1] = Checksum(tx);
        stream.WriteTimeout = 300;
        stream.Write(tx);

        // Драйвер производителя опрашивает тот же канал — его ответы тоже придут сюда, они нам подходят.
        var rx = new byte[PacketLength];
        stream.ReadTimeout = 100;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            int n;
            try { n = stream.Read(rx, 0, rx.Length); }
            catch (TimeoutException) { continue; }
            if (n == PacketLength && rx[1] == command && rx[PacketLength - 1] == Checksum(rx)) return rx;
        }
        return null;
    }

    private static byte Checksum(byte[] packet)
    {
        int sum = 0;
        for (int i = 1; i < PacketLength - 1; i++) sum += packet[i];
        return (byte)sum;
    }
}
