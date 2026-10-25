using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Logitech HID++ 2.0: мыши/клавиатуры через ресивер Lightspeed/Unifying или по проводу.
/// Протокол описан в Solaar и libratbag.
/// </summary>
/// <remarks>
/// На Windows «короткие» (0x10, 7 байт) и «длинные» (0x11, 20 байт) отчёты HID++ — разные HID-коллекции.
/// Запросы шлём длинными, а ответ ресивера «устройство недоступно» (0x8F, код 0x08) приходит коротким,
/// поэтому слушаем обе. Во сне мышь продолжает отвечать; явная ошибка значит, что она выключена.
/// </remarks>
internal sealed class LogitechProvider : IBatteryProvider
{
    private const int VendorId = 0x046D;
    private const int ShortReportLength = 7;
    private const int LongReportLength = 20;
    private static readonly TimeSpan BatteryRefresh = TimeSpan.FromSeconds(30);

    // Кэш имени и типа: имя меняться не будет. Заряд перечитываем не чаще BatteryRefresh, а связь проверяем каждый опрос.
    private readonly Dictionary<string, (string Name, DeviceKind Kind)> _info = new();
    private readonly Dictionary<string, ((int? Percent, ChargeState State) Value, DateTime Read)> _battery = new();
    private List<string> _offline = new();

    public string Name => "logitech";

    public IReadOnlyCollection<string> Offline => _offline;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(5);

    public IEnumerable<DeviceBattery> Poll()
    {
        _offline = new List<string>();
        var result = new List<DeviceBattery>();
        var all = DeviceList.Local.GetHidDevices(VendorId).ToList();
        var shorts = all.Where(d => HasLengths(d, ShortReportLength)).ToList();

        foreach (var dev in all.Where(d => HasLengths(d, LongReportLength)))
        {
            try
            {
                var sibling = shorts.FirstOrDefault(s => InterfaceKey(s.DevicePath) == InterfaceKey(dev.DevicePath));
                PollInterface(dev, sibling, result);
            }
            catch (Exception ex)
            {
                Log.Error($"logitech {dev.ProductID:X4}", ex);
            }
        }
        return result;
    }

    private static bool HasLengths(HidDevice d, int length)
    {
        try
        {
            return d.GetMaxOutputReportLength() == length && d.GetMaxInputReportLength() == length;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Путь без номера коллекции: у короткой и длинной коллекции одного интерфейса он совпадает.</summary>
    private static string InterfaceKey(string path) =>
        Regex.Replace(Regex.Replace(path, "&col[0-9a-f]+", "", RegexOptions.IgnoreCase), "&[0-9a-f]{4}#", "#", RegexOptions.IgnoreCase);

    private void PollInterface(HidDevice longDev, HidDevice? shortDev, List<DeviceBattery> result)
    {
        if (!longDev.TryOpen(out var longStream)) return;
        HidStream? shortStream = null;
        if (shortDev != null && !shortDev.TryOpen(out shortStream))
            shortStream = null;

        using (longStream)
        using (shortStream)
        {
            longStream.WriteTimeout = 250;
            var hidpp = new Hidpp(longStream, shortStream);

            // 0xFF — устройство подключено напрямую (по проводу). Иначе перебираем слоты ресивера.
            IEnumerable<byte> indices = hidpp.Ping(0xFF) == Link.Online ? new byte[] { 0xFF } : new byte[] { 1, 2, 3, 4, 5, 6 };
            foreach (byte idx in indices)
            {
                string id = $"logi:{longDev.DevicePath}|{idx}";
                if (idx != 0xFF)
                {
                    var link = hidpp.Ping(idx);
                    if (link == Link.Offline) _offline.Add(id);
                    if (link != Link.Online) continue;
                }

                if (!_battery.TryGetValue(id, out var cached) || DateTime.UtcNow - cached.Read > BatteryRefresh)
                {
                    if (hidpp.ReadBattery(idx) is not { } fresh) continue;
                    cached = (fresh, DateTime.UtcNow);
                    _battery[id] = cached;
                }

                if (!_info.TryGetValue(id, out var info))
                {
                    info = (hidpp.ReadName(idx) ?? "Logitech", hidpp.ReadKind(idx));
                    _info[id] = info;
                }

                result.Add(new DeviceBattery(id, info.Name, info.Kind, cached.Value.Percent, cached.Value.State));
            }
        }
    }

    private enum Link { Online, Offline, Unknown }

    private sealed class Hidpp
    {
        private const byte SwId = 0x0B;
        private const byte RootFeature = 0x00;
        private const byte ErrorHidpp10 = 0x8F;
        private const byte ErrorHidpp20 = 0xFF;
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(500);

        private readonly HidStream _long;
        private readonly HidStream? _short;
        private readonly Dictionary<(byte, ushort), byte> _featureIndex = new();

        public Hidpp(HidStream longStream, HidStream? shortStream)
        {
            _long = longStream;
            _short = shortStream;
        }

        public Link Ping(byte device)
        {
            var (ok, r) = Request(device, RootFeature, 1, 0, 0, 0x5A);
            if (ok == Reply.Ok) return r![0] >= 2 && r[2] == 0x5A ? Link.Online : Link.Unknown;
            return ok == Reply.Error ? Link.Offline : Link.Unknown;
        }

        public (int? Percent, ChargeState State)? ReadBattery(byte device)
        {
            // 0x1004 Unified Battery — современные устройства, в т.ч. Pro X Superlight 2.
            byte idx = Feature(device, 0x1004);
            if (idx != 0 && Call(device, idx, 1) is { } u)
            {
                var state = u[2] switch
                {
                    0 => ChargeState.Discharging,
                    1 or 2 => ChargeState.Charging,
                    3 => ChargeState.Full,
                    _ => ChargeState.Unknown,
                };
                return (u[0], state);
            }

            // 0x1000 Battery Status — старые устройства.
            idx = Feature(device, 0x1000);
            if (idx != 0 && Call(device, idx, 0) is { } b)
            {
                var state = b[2] switch
                {
                    0 => ChargeState.Discharging,
                    1 or 2 or 4 => ChargeState.Charging,
                    3 => ChargeState.Full,
                    _ => ChargeState.Unknown,
                };
                return (b[0] is > 0 and <= 100 ? b[0] : null, state);
            }

            // 0x1001 Battery Voltage — процент считаем по напряжению.
            idx = Feature(device, 0x1001);
            if (idx != 0 && Call(device, idx, 0) is { } v)
            {
                int mv = (v[0] << 8) | v[1];
                bool charging = (v[2] & 0x80) != 0;
                var state = !charging ? ChargeState.Discharging
                    : (v[2] & 0x07) == 1 ? ChargeState.Full
                    : ChargeState.Charging;
                return (VoltageToPercent(mv), state);
            }

            // 0x1F20 ADC Measurement — гарнитуры Logitech G (G535, G733, Pro X): напряжение и флаги.
            idx = Feature(device, 0x1F20);
            if (idx != 0 && Call(device, idx, 0) is { } adc && (adc[2] & 0x01) != 0)
            {
                int mv = (adc[0] << 8) | adc[1];
                var state = (adc[2] & 0x02) != 0 ? ChargeState.Charging : ChargeState.Discharging;
                return (VoltageToPercent(mv), state);
            }

            return null;
        }

        public string? ReadName(byte device)
        {
            byte idx = Feature(device, 0x0005);
            if (idx == 0 || Call(device, idx, 0) is not { } count) return null;

            int length = count[0];
            var bytes = new List<byte>();
            while (bytes.Count < length)
            {
                var chunk = Call(device, idx, 1, (byte)bytes.Count);
                if (chunk == null) break;
                int take = Math.Min(length - bytes.Count, chunk.Length);
                if (take <= 0) break;
                bytes.AddRange(chunk.Take(take));
            }
            var name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
            return name.Length > 0 ? name : null;
        }

        public DeviceKind ReadKind(byte device)
        {
            byte idx = Feature(device, 0x0005);
            if (idx == 0 || Call(device, idx, 2) is not { } t) return DeviceKind.Mouse;
            return t[0] switch
            {
                0 or 2 => DeviceKind.Keyboard,
                3 or 4 or 5 => DeviceKind.Mouse,
                8 => DeviceKind.Headset,
                11 or 12 => DeviceKind.Gamepad,
                _ => DeviceKind.Other,
            };
        }

        private byte Feature(byte device, ushort featureId)
        {
            if (_featureIndex.TryGetValue((device, featureId), out var cached)) return cached;
            byte idx = Call(device, RootFeature, 0, (byte)(featureId >> 8), (byte)featureId)?[0] ?? 0;
            _featureIndex[(device, featureId)] = idx;
            return idx;
        }

        private byte[]? Call(byte device, byte feature, byte function, params byte[] args)
        {
            var (reply, data) = Request(device, feature, function, args);
            return reply == Reply.Ok ? data : null;
        }

        private enum Reply { Ok, Error, Timeout }

        /// <summary>Отправляет запрос и ждёт ответ с тем же устройством/функцией на любой из коллекций.</summary>
        private (Reply, byte[]?) Request(byte device, byte feature, byte function, params byte[] args)
        {
            var tx = new byte[LongReportLength];
            tx[0] = 0x11;
            tx[1] = device;
            tx[2] = feature;
            tx[3] = (byte)((function << 4) | SwId);
            Array.Copy(args, 0, tx, 4, Math.Min(args.Length, 16));
            _long.Write(tx);

            var longBuf = new byte[LongReportLength];
            var shortBuf = new byte[ShortReportLength];
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < ResponseTimeout)
            {
                if (TryRead(_long, longBuf, out int n) && Match(longBuf, n, tx) is { } fromLong)
                    return fromLong;
                if (_short != null && TryRead(_short, shortBuf, out n) && Match(shortBuf, n, tx) is { } fromShort)
                    return fromShort;
            }
            return (Reply.Timeout, null);
        }

        private static bool TryRead(HidStream stream, byte[] buf, out int n)
        {
            stream.ReadTimeout = 25;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
                return n > 0;
            }
            catch (TimeoutException)
            {
                n = 0;
                return false;
            }
        }

        private static (Reply, byte[]?)? Match(byte[] rx, int n, byte[] tx)
        {
            if (n < 6 || rx[1] != tx[1]) return null;
            // Ошибки HID++ 1.0 (0x8F) и 2.0 (0xFF): [id, dev, 8F/FF, feature, func|sw, code]
            if ((rx[2] == ErrorHidpp10 || rx[2] == ErrorHidpp20) && rx[3] == tx[2] && rx[4] == tx[3]) return (Reply.Error, null);
            if (rx[2] == tx[2] && rx[3] == tx[3]) return (Reply.Ok, rx[4..n]);
            return null;
        }

        private static readonly (int Mv, int Pct)[] VoltageCurve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        };

        private static int VoltageToPercent(int mv)
        {
            if (mv >= VoltageCurve[0].Mv) return 100;
            for (int i = 1; i < VoltageCurve.Length; i++)
            {
                var (hiMv, hiPct) = VoltageCurve[i - 1];
                var (loMv, loPct) = VoltageCurve[i];
                if (mv >= loMv)
                    return loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv);
            }
            return 0;
        }
    }
}
