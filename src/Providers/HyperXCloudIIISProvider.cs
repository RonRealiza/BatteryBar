using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// HyperX Cloud III S Wireless через 2.4 ГГц донгл. Протокол другой, чем у Cloud III Wireless, — из проекта HyperHeadset
/// (cloud_iii_s_wireless): отчёт 0x0C на 64 байта с заголовком 0C 02 03 01 00, команда в байте 5, значение ответа в байте 6.
/// Команды: 0x02 — связь с гарнитурой (2 = есть), 0x06 — заряд, 0x48 — зарядка (0 нет, 1 идёт, 2 полностью).
/// </summary>
internal sealed class HyperXCloudIIISProvider : IBatteryProvider
{
    private const int HpVendorId = 0x03F0;
    private static readonly int[] ProductIds = { 0x06BE, 0x02CC };
    private const int PacketLength = 64;
    private const byte ReportId = 0x0C;

    private const byte CmdConnected = 0x02;
    private const byte CmdBattery = 0x06;
    private const byte CmdCharging = 0x48;
    private const byte NoData = 0xFF;

    private string? _lastRaw;

    public string Name => "hyperx-s";

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        // У донгла несколько коллекций; пишем только в ту, что принимает 64-байтные отчёты:
        // по опыту HyperHeadset, лишние запросы во все коллекции сбивают донгл с толку.
        var dev = DeviceList.Local.GetHidDevices(HpVendorId).FirstOrDefault(IsControlInterface);
        if (dev == null) return result;
        try
        {
            if (!dev.TryOpen(out var stream)) return result;
            using (stream)
            {
                stream.ReadTimeout = 300;
                stream.WriteTimeout = 300;

                var link = Request(stream, CmdConnected);
                var battery = link is { } l && l[6] == 2 ? Request(stream, CmdBattery) : null;
                var charging = battery != null ? Request(stream, CmdCharging) : null;

                // Сырые ответы — в журнал при изменении: протокол на настоящей гарнитуре здесь ещё не проверен.
                string Hex(byte[]? r) => r == null ? "none" : string.Join(" ", r.Take(8).Select(b => b.ToString("X2")));
                string raw = $"link {Hex(link)}; battery {Hex(battery)}; charging {Hex(charging)}";
                if (raw != _lastRaw)
                {
                    Log.Write($"cloud iii s raw: {raw}");
                    _lastRaw = raw;
                }

                // Гарнитура выключена или донгл ответил «нет данных».
                if (battery is not { } b || b[6] == NoData) return result;
                var state = charging?[6] switch
                {
                    0x00 => ChargeState.Discharging,
                    0x01 => ChargeState.Charging,
                    0x02 => ChargeState.Full,
                    _ => ChargeState.Unknown,
                };
                result.Add(new DeviceBattery($"hyperx:{dev.ProductID:X4}", "HyperX Cloud III S", DeviceKind.Headset,
                    Math.Clamp((int)b[6], 0, 100), state));
            }
        }
        catch (Exception ex)
        {
            Log.Error("hyperx-s", ex);
        }
        return result;
    }

    private static bool IsControlInterface(HidDevice d)
    {
        if (!ProductIds.Contains(d.ProductID)) return false;
        try
        {
            return d.GetMaxOutputReportLength() == PacketLength;
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
        tx[1] = 0x02;
        tx[2] = 0x03;
        tx[3] = 0x01; // 1 — запрос значения, 0 — установка
        tx[5] = command;
        try
        {
            stream.Write(tx);
        }
        catch (System.IO.IOException)
        {
            // Как у HyperHeadset: если донгл не принимает выходной отчёт, тот же пакет — feature-отчётом.
            stream.SetFeature(tx);
        }

        // Ответы на запросы и уведомления (0x0D), а также кнопки и микрофон идут одним потоком — ждём свой.
        var rx = new byte[PacketLength];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 600)
        {
            int n;
            try { n = stream.Read(rx, 0, rx.Length); }
            catch (TimeoutException) { return null; }
            if (n >= 7 && rx[0] == ReportId && rx[5] == command) return rx;
        }
        return null;
    }
}
