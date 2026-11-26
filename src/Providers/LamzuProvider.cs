using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Мышь LAMZU Maya X: ресивер 8K (373E:001E), ресивер 1K (373E:001D) и кабель (373E:001C).
/// Протокол тот же, что у LAMZU Aurora для моделей с IsNewProtocol: коллекция FFFF:0000,
/// feature-отчёт на 64 байта (номер 0). В массиве без номера отчёта Aurora ставит
/// [2]=0x02, [3]=0x02, [5]=0x83 — только чтение заряда. HidSharp оставляет номер отчёта
/// в [0], поэтому в буфере на 65 байт это байты [3], [4] и [6]. Ответ: [1]=0xA1 (мышь на связи),
/// [6]=0x83, [7] — зарядка (1), [8] — процент. Ноль — мышь выключена или вне зоны ресивера.
/// Проверено на Maya X с ресивером 8K и по кабелю: по кабелю тот же ответ, байт зарядки равен 1.
/// </summary>
internal sealed class LamzuProvider : IBatteryProvider
{
    private const int VendorId = 0x373E;
    // 001C — кабель, 001D — ресивер 1K, 001E — ресивер 8K. Одна и та же команда в Aurora.
    private static readonly int[] ProductIds = { 0x001C, 0x001D, 0x001E };
    private const int FeatureLength = 65;
    private const byte StatusAwake = 0xA1;
    private const byte CmdBattery = 0x83;

    private readonly List<string> _offline = new();
    private string? _lastRaw;

    public string Name => "lamzu";

    // Обмен с ресивером ждёт ответ по радио ~100 мс; чаще опрашивать незачем.
    public TimeSpan PollInterval => TimeSpan.FromSeconds(15);

    public IReadOnlyCollection<string> Offline => _offline;

    public IEnumerable<DeviceBattery> Poll()
    {
        _offline.Clear();
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId)
                     .Where(d => ProductIds.Contains(d.ProductID) && IsBatteryInterface(d)))
        {
            string id = $"lamzu:{dev.VendorID:X4}:{dev.ProductID:X4}";
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    // Первый ответ после сна иногда пустой — переспрашиваем один раз, как Aurora.
                    var rx = Query(stream);
                    if (rx == null || !TryParse(rx, out _, out _)) rx = Query(stream);
                    string raw = rx == null ? "no reply" : string.Join(" ", rx.Take(9).Select(b => b.ToString("X2")));
                    if (raw != _lastRaw)
                    {
                        Log.Write($"lamzu {dev.VendorID:X4}:{dev.ProductID:X4} raw: {raw}");
                        _lastRaw = raw;
                    }
                    if (rx == null || !TryParse(rx, out int charging, out int percent)) continue;
                    // Ресивер явно ответил, что мыши на связи нет. Сон приходит другим статусом и сюда не попадает:
                    // тогда BatteryMonitor оставляет последний заряд.
                    if (percent == 0)
                    {
                        _offline.Add(id);
                        continue;
                    }
                    if (percent > 100) continue;
                    var state = charging == 1 ? ChargeState.Charging
                        : percent >= 100 ? ChargeState.Full
                        : ChargeState.Discharging;
                    result.Add(new DeviceBattery(id, "LAMZU Maya X", DeviceKind.Mouse, percent, state));
                }
            }
            catch (Exception ex)
            {
                Log.Error("lamzu", ex);
            }
        }
        return result;
    }

    /// <summary>Служебная коллекция ресивера: страница FFFF, использование 0, feature-отчёт на 64 байта.</summary>
    private static bool IsBatteryInterface(HidDevice d)
    {
        try
        {
            return d.GetMaxFeatureReportLength() == FeatureLength
                   && d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u == 0xFFFF0000));
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? Query(HidStream stream)
    {
        var tx = new byte[FeatureLength];
        tx[3] = 0x02;
        tx[4] = 0x02;
        tx[6] = CmdBattery;
        stream.SetFeature(tx);
        Thread.Sleep(100);
        var rx = new byte[FeatureLength];
        stream.GetFeature(rx);
        return rx;
    }

    /// <summary>
    /// Два вида ответа: с номером отчёта в [0] (HidSharp и WebHID) и без него.
    /// Возвращает false, если это не ответ на 0x83 — мышь спит (статус не 0xA1) или ответ чужой.
    /// </summary>
    private static bool TryParse(byte[] rx, out int charging, out int percent)
    {
        if (rx.Length >= 9 && rx[1] == StatusAwake && rx[4] == 0x02 && rx[6] == CmdBattery)
        {
            charging = rx[7];
            percent = rx[8];
            return true;
        }
        if (rx.Length >= 8 && rx[0] == StatusAwake && rx[3] == 0x02 && rx[5] == CmdBattery)
        {
            charging = rx[6];
            percent = rx[7];
            return true;
        }
        charging = 0;
        percent = 0;
        return false;
    }
}
