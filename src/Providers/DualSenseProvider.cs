using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// DualSense / DualSense Edge. Заряд лежит в байте статуса входного отчёта
/// (формат как в драйвере Linux hid-playstation).
/// </summary>
internal sealed class DualSenseProvider : IBatteryProvider
{
    private const int SonyVendorId = 0x054C;
    private static readonly int[] ProductIds = { 0x0CE6, 0x0DF2 };

    public string Name => "dualsense";

    public IEnumerable<DeviceBattery> Poll()
    {
        var found = new List<(HidDevice Dev, byte Status)>();
        foreach (var dev in DeviceList.Local.GetHidDevices(SonyVendorId).Where(d => ProductIds.Contains(d.ProductID)))
        {
            try
            {
                if (ReadStatus(dev) is { } status) found.Add((dev, status));
            }
            catch (Exception ex)
            {
                Log.Error("dualsense", ex);
            }
        }

        // Подпись «PS» у значка; если геймпадов несколько — с номером по порядку подключения.
        var result = new List<DeviceBattery>();
        for (int i = 0; i < found.Count; i++)
        {
            var (dev, status) = found[i];
            string suffix = found.Count > 1 ? $"{i + 1}" : "";
            string name = (dev.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense") + (suffix.Length > 0 ? $" {suffix}" : "");
            var (percent, state) = Decode(status);
            result.Add(new DeviceBattery($"ds:{dev.DevicePath}", name, DeviceKind.Gamepad, percent, state, $"PS{suffix}"));
        }
        return result;
    }

    private static byte? ReadStatus(HidDevice dev)
    {
        int inLen = dev.GetMaxInputReportLength();
        bool bluetooth = inLen > 64; // USB — 64 байта, Bluetooth — 78

        if (!dev.TryOpen(out var stream)) return null;
        using (stream)
        {
            stream.ReadTimeout = 200;

            if (bluetooth)
            {
                // По Bluetooth геймпад сначала шлёт урезанный отчёт 0x01 без заряда.
                // Чтение калибровки (feature 0x05) переключает его на полный отчёт 0x31 — так делают Steam и DS4Windows.
                var feature = new byte[dev.GetMaxFeatureReportLength()];
                feature[0] = 0x05;
                try { stream.GetFeature(feature); }
                catch (Exception ex) { Log.Error("dualsense feature", ex); }
            }

            var buf = new byte[inLen];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }

                if (!bluetooth && buf[0] == 0x01 && n > 53) return buf[53];
                if (bluetooth && buf[0] == 0x31 && n > 54) return buf[54];
            }
        }
        return null;
    }

    private static (int? Percent, ChargeState State) Decode(byte status)
    {
        int level = status & 0x0F;
        int charging = status >> 4;
        int percent = Math.Min(level * 10 + 5, 100);
        return charging switch
        {
            0x0 => (percent, ChargeState.Discharging),
            0x1 => (percent, ChargeState.Charging),
            0x2 => (100, ChargeState.Full),
            _ => (null, ChargeState.Unknown),
        };
    }
}
