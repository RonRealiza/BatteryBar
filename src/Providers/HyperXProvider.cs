using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// HyperX Cloud III Wireless через 2.4 ГГц донгл. Протокол — из проекта HyperHeadset (cloud_iii_wireless):
/// отчёт 0x66 на 62 байта, байт 1 — команда, ответ приходит с тем же номером команды.
/// </summary>
internal sealed class HyperXProvider : IBatteryProvider
{
    private const int HpVendorId = 0x03F0;
    private static readonly int[] ProductIds = { 0x05B7, 0x0C9D };
    private const int PacketLength = 62;
    private const byte ReportId = 0x66;

    private const byte CmdWirelessStatus = 0x82;
    private const byte CmdBattery = 0x89;
    private const byte CmdCharging = 0x8A;

    public string Name => "hyperx";

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(HpVendorId).Where(IsControlInterface))
        {
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;

                    // Донгл отвечает и при выключенной гарнитуре — проверяем, есть ли связь с ней.
                    if (Request(stream, CmdWirelessStatus) is not { } link || link[2] != 0x01) continue;
                    if (Request(stream, CmdBattery) is not { } battery) continue;

                    int percent = Math.Clamp((int)battery[4], 0, 100);
                    var state = Request(stream, CmdCharging)?[2] switch
                    {
                        0x00 => ChargeState.Discharging,
                        0x01 => ChargeState.Charging,
                        0x02 => ChargeState.Full,
                        _ => ChargeState.Unknown,
                    };
                    result.Add(new DeviceBattery($"hyperx:{dev.ProductID:X4}", "HyperX Cloud III", DeviceKind.Headset, percent, state));
                }
            }
            catch (Exception ex)
            {
                Log.Error("hyperx", ex);
            }
        }
        return result;
    }

    private static bool IsControlInterface(HidDevice d)
    {
        if (!ProductIds.Contains(d.ProductID)) return false;
        try
        {
            return d.GetMaxOutputReportLength() == PacketLength && d.GetMaxInputReportLength() == PacketLength;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? Request(HidStream stream, byte command)
    {
        var tx = new byte[PacketLength];
        tx[0] = ReportId;
        tx[1] = command;
        stream.Write(tx);

        var rx = new byte[PacketLength];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 600)
        {
            int n;
            try { n = stream.Read(rx, 0, rx.Length); }
            catch (TimeoutException) { return null; }
            if (n >= 5 && rx[0] == ReportId && rx[1] == command) return rx;
        }
        return null;
    }
}
