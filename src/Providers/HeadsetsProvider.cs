using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Беспроводные гарнитуры SteelSeries, HyperX и Corsair. Протоколы — по описаниям из открытого проекта
/// HeadsetControl (github.com/Sapd/HeadsetControl): какие байты отправить и где в ответе заряд.
/// </summary>
/// <remarks>
/// HeadsetControl работает через hidapi, а она на Windows отбрасывает нулевой номер отчёта в начале ответа.
/// HidSharp его оставляет, поэтому у моделей с запросом «00 xx» номера байтов ответа здесь на 1 больше.
/// Команды шлём только устройствам, точно опознанным по VID/PID (и интерфейсу), — чужим ничего не отправляем.
/// </remarks>
internal sealed class HeadsetsProvider : IBatteryProvider
{
    private delegate (int Percent, ChargeState State)? Query(HidStream stream, HidDevice dev);

    private sealed record Model(string Name, int VendorId, int[] ProductIds, int? Interface, uint? UsagePage, Query Read);

    private const int SteelSeries = 0x1038;
    private const int Hp = 0x03F0;
    private const int Kingston = 0x0951;
    private const int Corsair = 0x1B1C;

    // У этих Nova 7 до обновления прошивки заряд не в процентах, а ступенями 0–4.
    private static readonly int[] Nova7Discrete = { 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4 };

    private static readonly Model[] Models =
    {
        new("SteelSeries Arctis Nova 7", SteelSeries,
            new[] { 0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E, 0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7, 0x2298 },
            3, 0xFFC0, (s, d) =>
            {
                if (Exchange(s, new byte[] { 0x00, 0xB0 }, 128) is not { } r || r.Length < 5) return null;
                if (r[4] == 0x00) return null; // гарнитура выключена
                int level = Nova7Discrete.Contains(d.ProductID) ? Map(r[3], 0, 4) : Math.Min((int)r[3], 100);
                return (level, r[4] is 0x01 or 0x02 ? ChargeState.Charging : ChargeState.Discharging);
            }),

        new("SteelSeries Arctis 7+", SteelSeries, new[] { 0x220E, 0x2212, 0x2216, 0x2236 }, 3, 0xFFC0, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x00, 0xB0 }, 128) is not { } r || r.Length < 5 || r[2] == 0x01) return null;
            return (Map(r[3], 0, 4), r[4] == 0x01 ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("SteelSeries Arctis Nova 5", SteelSeries, new[] { 0x2232, 0x2253 }, 3, 0xFFC0, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x00, 0xB0 }, 128) is not { } r || r.Length < 17 || r[2] == 0x02) return null;
            return (Math.Min((int)r[4], 100), r[5] == 0x01 ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("SteelSeries Arctis Nova Pro Wireless", SteelSeries, new[] { 0x12E0, 0x12E5 }, 4, null, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x06, 0xB0 }, 128) is not { } r || r.Length < 16 || r[15] == 0x01) return null;
            return (Map(r[6], 0, 8), r[15] == 0x02 ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("SteelSeries Arctis 7", SteelSeries, new[] { 0x1260, 0x12AD, 0x1252, 0x1280 }, 5, null, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x06, 0x18 }, 8) is not { } r || r.Length < 3) return null;
            return (Math.Min((int)r[2], 100), ChargeState.Unknown);
        }),

        new("SteelSeries Arctis 1", SteelSeries, new[] { 0x12B3, 0x12B6, 0x12D7, 0x12D5 }, 3, 0xFF43, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x06, 0x12 }, 8) is not { } r || r.Length < 4 || r[2] == 0x01) return null;
            return (Math.Min((int)r[3], 100), ChargeState.Unknown);
        }),

        new("SteelSeries Arctis 9", SteelSeries, new[] { 0x12C2, 0x12C4 }, 0, null, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x00, 0x20 }, 12) is not { } r || r.Length < 6) return null;
            return (Map(r[4], 0x64, 0x9A), r[5] == 0x01 ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("HyperX Cloud Alpha Wireless", Hp, new[] { 0x098D }, null, null, (s, _) =>
        {
            byte[]? Cmd(byte c) => Exchange(s, new byte[] { 0x21, 0xBB, c }, 31, r => r[0] == 0x21 && r[1] == 0xBB && r[2] == c);
            if (Cmd(0x03) is not { } link || link[3] == 0x01) return null; // 0x01 — гарнитура не на связи
            if (Cmd(0x0B) is not { } level) return null;
            bool charging = Cmd(0x0C) is { } ch && ch[3] == 0x01;
            return (Math.Min((int)level[3], 100), charging ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("HyperX Cloud II Wireless", Hp, new[] { 0x0696, 0x018B }, 0, 0xFF90, (s, _) =>
        {
            byte[]? Cmd(byte c) => Exchange(s, new byte[] { 0x06, 0xFF, 0xBB, c }, 20,
                r => r[0] == 0x06 && r[1] == 0xFF && r[2] == 0xBB && r[3] == c);
            if (Cmd(0x02) is not { } level || level.Length < 8) return null;
            bool charging = Cmd(0x03) is { } ch && ch[4] == 0x01;
            return (Math.Min((int)level[7], 100), charging ? ChargeState.Charging : ChargeState.Discharging);
        }),

        new("HyperX Cloud Flight", Kingston, new[] { 0x16C4, 0x1723 }, null, null, (s, _) =>
        {
            if (Exchange(s, new byte[] { 0x21, 0xFF, 0x05 }, 20) is not { } r || r.Length < 5) return null;
            int mv = (r[3] << 8) | r[4];
            if (mv == 0) return null;
            // Выше ~4107 мВ гарнитура на зарядке — процент по напряжению тогда завышен.
            return mv > 4107 ? (100, ChargeState.Charging) : (FlightPercent(mv), ChargeState.Discharging);
        }),

        new("Corsair Void", Corsair,
            new[] { 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 },
            3, 0xFFC5, (s, _) =>
            {
                if (Exchange(s, new byte[] { 0xC9, 0x64 }, 5) is not { } r || r.Length < 5) return null;
                int level = Math.Min(r[2] & 0x7F, 100); // старший бит — поднят ли микрофон
                return r[4] switch
                {
                    1 or 2 => (level, ChargeState.Discharging),
                    4 or 5 => (level, ChargeState.Charging),
                    _ => null, // 0 — гарнитура не на связи
                };
            }),
    };

    public string Name => "headsets";

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        // У гарнитуры бывает несколько подходящих HID-коллекций — берём первую ответившую.
        var answered = new HashSet<(int, int)>();
        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            var model = Models.FirstOrDefault(m => m.VendorId == dev.VendorID && m.ProductIds.Contains(dev.ProductID));
            if (model == null || answered.Contains((dev.VendorID, dev.ProductID)) || !Matches(dev, model)) continue;
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;
                    if (model.Read(stream, dev) is not { } battery) continue;
                    answered.Add((dev.VendorID, dev.ProductID));
                    result.Add(new DeviceBattery($"hs:{dev.DevicePath}", model.Name, DeviceKind.Headset, battery.Percent, battery.State));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"headset {dev.VendorID:X4}:{dev.ProductID:X4}", ex);
            }
        }
        return result;
    }

    /// <summary>Нужный интерфейс (MI_xx) и, если указана, vendor-страница usage — иначе не та коллекция.</summary>
    private static bool Matches(HidDevice dev, Model model)
    {
        string path = dev.DevicePath.ToLowerInvariant();
        if (model.Interface is { } mi && !(path.Contains($"mi_{mi:x2}") || (mi == 0 && !path.Contains("mi_"))))
            return false;
        try
        {
            if (dev.GetMaxOutputReportLength() == 0) return false;
            if (model.UsagePage is { } page &&
                !dev.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u >> 16 == page)))
                return false;
        }
        catch
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Отправляет запрос (дополняя нулями до длины отчёта) и ждёт подходящий ответ.
    /// </summary>
    private static byte[]? Exchange(HidStream stream, byte[] request, int minReadLength, Func<byte[], bool>? match = null)
    {
        var dev = stream.Device;
        var tx = new byte[dev.GetMaxOutputReportLength()];
        Array.Copy(request, tx, Math.Min(request.Length, tx.Length));
        stream.Write(tx);

        // По умолчанию ждём ответ с тем же номером отчёта (у «00 xx» — любой отчёт без номера).
        match ??= r => r[0] == request[0];

        var rx = new byte[Math.Max(dev.GetMaxInputReportLength(), minReadLength)];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000)
        {
            int n;
            try { n = stream.Read(rx, 0, rx.Length); }
            catch (TimeoutException) { return null; }
            if (n < 2) continue;
            var reply = rx[..n];
            if (match(reply)) return reply;
        }
        return null;
    }

    private static int Map(int value, int min, int max) =>
        Math.Clamp((value - min) * 100 / Math.Max(1, max - min), 0, 100);

    /// <summary>Кривая напряжение → процент для HyperX Cloud Flight (из HeadsetControl).</summary>
    private static int FlightPercent(int mv)
    {
        if (mv <= 3648) return (int)Math.Round(0.00125 * mv);
        if (mv > 3975) return 100;
        double v = mv;
        double p = 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3)
                   + 2.238321 * v * v - 5706.256 * v + 5452299.0;
        return Math.Clamp((int)Math.Round(p), 0, 100);
    }
}
