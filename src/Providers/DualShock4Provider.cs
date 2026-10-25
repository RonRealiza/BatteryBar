using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// DualShock 4 (PS4): обе ревизии и фирменный USB-адаптер. Заряд — в байте статуса входного отчёта
/// (формат как в драйвере Linux hid-playstation): младшие 4 бита — уровень, бит 0x10 — подключён кабель.
/// </summary>
internal sealed class DualShock4Provider : IBatteryProvider
{
    private const int SonyVendorId = 0x054C;
    private const int Adapter = 0x0BA0;
    private static readonly int[] ProductIds = { 0x05C4, 0x09CC, Adapter };

    public string Name => "dualshock4";

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
                Log.Error("dualshock4", ex);
            }
        }

        // Подпись «PS4» у значка; если геймпадов несколько — с номером по порядку подключения.
        var result = new List<DeviceBattery>();
        for (int i = 0; i < found.Count; i++)
        {
            var (dev, status) = found[i];
            string suffix = found.Count > 1 ? $"{i + 1}" : "";
            var (percent, state) = Decode(status);
            result.Add(new DeviceBattery($"ds4:{dev.DevicePath}", "DualShock 4" + (suffix.Length > 0 ? $" {suffix}" : ""),
                DeviceKind.Gamepad, percent, state, $"PS4{suffix}"));
        }
        return result;
    }

    private static byte? ReadStatus(HidDevice dev)
    {
        int inLen = dev.GetMaxInputReportLength();
        bool bluetooth = inLen > 64; // USB — 64 байта, по Bluetooth отчёты длиннее

        if (!dev.TryOpen(out var stream)) return null;
        using (stream)
        {
            stream.ReadTimeout = 200;

            if (bluetooth)
            {
                // По Bluetooth сначала идёт урезанный отчёт 0x01 без заряда; чтение калибровки (feature 0x05)
                // переключает геймпад на полный отчёт 0x11 — так делают Steam и DS4Windows.
                var feature = new byte[dev.GetMaxFeatureReportLength()];
                feature[0] = 0x05;
                try { stream.GetFeature(feature); }
                catch (Exception ex) { Log.Error("dualshock4 feature", ex); }
            }

            var buf = new byte[inLen];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); }
                catch (TimeoutException) { continue; }

                if (!bluetooth && buf[0] == 0x01 && n > 30)
                {
                    // Адаптер без геймпада шлёт нули вместо положения стиков (в покое у живого геймпада ~0x80).
                    if (dev.ProductID == Adapter && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 && buf[4] == 0) return null;
                    return buf[30];
                }
                if (bluetooth && buf[0] == 0x11 && n > 32) return buf[32];
            }
        }
        return null;
    }

    private static (int? Percent, ChargeState State) Decode(byte status)
    {
        int level = status & 0x0F;
        bool cable = (status & 0x10) != 0;
        if (!cable)
            return (level < 10 ? level * 10 + 5 : 100, ChargeState.Discharging);
        return level switch
        {
            < 10 => (level * 10 + 5, ChargeState.Charging),
            10 => (100, ChargeState.Charging),
            11 => (100, ChargeState.Full),
            _ => (null, ChargeState.Unknown),
        };
    }
}
