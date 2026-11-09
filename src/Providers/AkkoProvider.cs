using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Клавиатуры Akko и MonsGeek на чипах RongYuan (в том числе магнитные, например Akko 5075 V5 TMR) через 2.4 ГГц ресивер.
/// Протокол — по monsgeek-akko-linux и Open-Akko: служебная коллекция 0xFFFF:0x0002, feature-отчёты по 64 байта
/// (номер отчёта 0). Команда ресиверу 0xF7 — его состояние: [0] есть ответ, [1] заряд 0–100, [3] идёт зарядка.
/// Ресивер отвечает сам, по последнему пакету от клавиатуры, — клавиатуре ничего не отправляется.
/// </summary>
/// <remarks>
/// Код ресивера 38EE (как у Akko 5075 V5 TMR) в этих проектах не описан — устройство канала совпадает,
/// поэтому пробуем тот же запрос, а сырые ответы пишем в журнал.
/// </remarks>
internal sealed class AkkoProvider : IBatteryProvider
{
    private const int AkkoVendorId = 0x38EE;
    private const int RongYuanVendorId = 0x3151;
    // Ресиверы MonsGeek/Akko с кодом 3151, для которых запрос описан (у самих клавиатур на проводе он означает другое).
    private static readonly int[] RongYuanDongles = { 0x4011, 0x5007, 0x5020, 0x5037, 0x5038, 0x503A, 0x503F, 0x5040, 0x504B };
    private const int FeatureLength = 65; // номер отчёта + 64 байта
    private const byte CmdDongleStatus = 0xF7;

    private string? _lastRaw;

    public string Name => "akko";

    public TimeSpan PollInterval => TimeSpan.FromSeconds(15);

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        var candidates = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == AkkoVendorId || (d.VendorID == RongYuanVendorId && RongYuanDongles.Contains(d.ProductID)))
            .Where(IsConfigInterface);
        foreach (var dev in candidates)
        {
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    var r = DongleStatus(stream);
                    string raw = r == null ? "no reply" : string.Join(" ", r.Skip(1).Take(10).Select(b => b.ToString("X2")));
                    if (raw != _lastRaw)
                    {
                        Log.Write($"akko {dev.VendorID:X4}:{dev.ProductID:X4} raw: {raw}");
                        _lastRaw = raw;
                    }

                    // Со смещением на номер отчёта: [2] — заряд, [7] — клавиатура на связи (1 или 3, как в Open-Akko).
                    // Признак «есть ответ» в [1] ресивер 38EE не выставляет (у Akko 5075 V5 TMR там 0), поэтому не смотрим на него.
                    // Сразу после включения ресивер отвечает нулевым зарядом, пока клавиатура его не прислала, — такой не показываем.
                    if (r == null || r[7] is not (1 or 3) || r[2] is < 1 or > 100) continue;
                    bool charging = r[4] == 1;
                    result.Add(new DeviceBattery($"akko:{dev.VendorID:X4}:{dev.ProductID:X4}", NameOf(dev), DeviceKind.Keyboard,
                        r[2], charging ? ChargeState.Charging : ChargeState.Discharging));
                    break; // у ресивера одна клавиатура
                }
            }
            catch (Exception ex)
            {
                Log.Error("akko", ex);
            }
        }
        return result;
    }

    /// <summary>Служебная коллекция: страница 0xFFFF, feature-отчёт на 64 байта.</summary>
    private static bool IsConfigInterface(HidDevice d)
    {
        try
        {
            return d.GetMaxFeatureReportLength() == FeatureLength
                   && d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u >> 16 == 0xFFFF));
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? DongleStatus(HidStream stream)
    {
        var tx = new byte[FeatureLength];
        tx[1] = CmdDongleStatus;
        // Контрольный байт команды: 255 − (сумма байтов 0..6 команды) — для F7 это 0x08.
        tx[8] = (byte)(255 - (CmdDongleStatus & 0xFF));
        stream.SetFeature(tx);

        // Ресивер отвечает из своей памяти почти сразу; на всякий случай — несколько попыток.
        // Последний ответ возвращаем, даже если он без признака «есть ответ», — он нужен журналу.
        var rx = new byte[FeatureLength];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(20);
            stream.GetFeature(rx);
            if (rx[7] is 1 or 3) break;
        }
        return rx;
    }

    /// <summary>«Akko 2.4G Wireless Keyboard» → «Akko»: название модели ресивер не сообщает.</summary>
    private static string NameOf(HidDevice d)
    {
        string name;
        try { name = d.GetProductName() ?? ""; }
        catch { name = ""; }
        name = name.Replace("2.4G", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Wireless", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Keyboard", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("Receiver", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
        return name.Length > 0 ? name : d.VendorID == AkkoVendorId ? "Akko" : "MonsGeek";
    }
}
