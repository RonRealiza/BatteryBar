using System;
using System.IO;
using System.Text;
using HidSharp;

namespace BatteryBar;

internal static class Diagnostics
{
    /// <summary>Пишет список HID-устройств в %LOCALAPPDATA%\BatteryBar\hid-devices.txt.</summary>
    public static void DumpHidDevices()
    {
        var sb = new StringBuilder();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            sb.AppendLine($"VID_{d.VendorID:X4} PID_{d.ProductID:X4}  {Safe(d.GetProductName)}");
            sb.AppendLine($"  in={Safe(() => d.GetMaxInputReportLength().ToString())} " +
                          $"out={Safe(() => d.GetMaxOutputReportLength().ToString())} " +
                          $"feature={Safe(() => d.GetMaxFeatureReportLength().ToString())}");
            sb.AppendLine($"  {d.DevicePath}");
        }

        Directory.CreateDirectory(Log.Directory);
        var path = Path.Combine(Log.Directory, "hid-devices.txt");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
    }
}
