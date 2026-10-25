using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryBar;

/// <summary>Устройство в списке «какое не определилось»: HID (по VID/PID) или Bluetooth.</summary>
internal sealed record ReportCandidate(string Title, int? VendorId, int? ProductId, string? BluetoothName, bool Connected)
{
    public override string ToString() => Title;
}

/// <summary>
/// Отчёт о неподдерживаемом устройстве для заявки на GitHub. Без серийных номеров и внутренних
/// идентификаторов Windows; имена владельцев из названий Bluetooth-устройств («Vladimir’s AirPods») убираются.
/// </summary>
internal static class DeviceReport
{
    public const string RepoUrl = "https://github.com/RonRealiza/BatteryBar";

    /// <summary>Подключённые HID-устройства (по одному на VID/PID) и сопряжённые Bluetooth-устройства.</summary>
    public static List<ReportCandidate> Candidates()
    {
        var list = new List<ReportCandidate>();
        foreach (var group in DeviceList.Local.GetHidDevices().GroupBy(d => (d.VendorID, d.ProductID)))
        {
            string name = group.Select(d => Safe(d.GetProductName)).FirstOrDefault(n => n.Length > 0) ?? "";
            if (name.Length == 0) name = L.ReportUnnamed;
            list.Add(new ReportCandidate($"{name}  ({group.Key.VendorID:X4}:{group.Key.ProductID:X4})",
                group.Key.VendorID, group.Key.ProductID, null, true));
        }

        foreach (var (name, connected) in BluetoothDevices())
            list.Add(new ReportCandidate($"{name}  (Bluetooth{(connected ? ", " + L.ReportConnected : "")})", null, null, name, connected));

        return list.OrderBy(c => c.BluetoothName != null).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string Build(ReportCandidate? selected, string comment, IReadOnlyList<DeviceBattery> shown)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BatteryBar {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}, {WindowsVersion()}");
        sb.AppendLine();

        if (selected == null)
        {
            sb.AppendLine("No device selected.");
        }
        else if (selected.VendorId is { } vid && selected.ProductId is { } pid)
        {
            sb.AppendLine($"Device: {selected.Title}");
            foreach (var d in DeviceList.Local.GetHidDevices(vid, pid))
                sb.AppendLine($"  {Interface(d.DevicePath)}  usage={Usage(d)}  in={Len(d.GetMaxInputReportLength)} out={Len(d.GetMaxOutputReportLength)} feature={Len(d.GetMaxFeatureReportLength)}  '{Safe(d.GetProductName)}' / '{Safe(d.GetManufacturer)}'");
        }
        else
        {
            sb.AppendLine($"Device: {selected.Title}");
        }

        if (comment.Trim().Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Comment: " + comment.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("BatteryBar shows: " + (shown.Count == 0 ? "nothing" : string.Join(", ",
            shown.Select(d => $"{StripOwner(d.Name)} {d.Percent?.ToString() ?? "?"}%"))));

        sb.AppendLine();
        sb.AppendLine("Other HID devices:");
        foreach (var group in DeviceList.Local.GetHidDevices().GroupBy(d => (d.VendorID, d.ProductID)).OrderBy(g => g.Key))
        {
            if (selected?.VendorId == group.Key.VendorID && selected.ProductId == group.Key.ProductID) continue;
            string name = group.Select(d => Safe(d.GetProductName)).FirstOrDefault(n => n.Length > 0) ?? "";
            sb.AppendLine($"  {group.Key.VendorID:X4}:{group.Key.ProductID:X4}  {name}  [{string.Join(" ", group.Select(d => Interface(d.DevicePath)).Distinct())}]");
        }

        var bt = BluetoothDevices();
        if (bt.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Bluetooth: " + string.Join(", ", bt.Select(b => b.Name + (b.Connected ? " (connected)" : ""))));
        }

        var log = LogTail();
        if (log.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Log (recent):");
            foreach (var line in log) sb.AppendLine("  " + line);
        }
        return sb.ToString();
    }

    /// <summary>Ссылка на новую заявку с заполненными полями шаблона new-device.yml.</summary>
    public static string IssueUrl(string title, string model, string report)
    {
        static string E(string s) => Uri.EscapeDataString(s);
        // Формы заявок на GitHub одноязычные — открываем шаблон на языке интерфейса.
        string template = L.En ? "new-device-en.yml" : "new-device.yml";
        return $"{RepoUrl}/issues/new?template={template}&labels=device-request" +
               $"&title={E(title)}&model={E(model)}&report={E(report)}";
    }

    private static List<(string Name, bool Connected)> BluetoothDevices()
    {
        var result = new List<(string, bool)>();
        try
        {
            foreach (string selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                var endpoints = DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.IsConnected" },
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask().GetAwaiter().GetResult();
                foreach (var e in endpoints)
                {
                    bool connected = e.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
                    result.Add((StripOwner(e.Name), connected));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("report bluetooth", ex);
        }
        return result.GroupBy(r => r.Item1).Select(g => (g.Key, g.Any(x => x.Item2))).ToList();
    }

    /// <summary>«Vladimir’s AirPods Pro 2» → «AirPods Pro 2».</summary>
    private static string StripOwner(string name) => Regex.Replace(name, @"^.+?[’'`]s\s+", "");

    /// <summary>Из пути устройства оставляем только интерфейс и коллекцию (mi_02&amp;col01) — без серийников.</summary>
    private static string Interface(string path)
    {
        var mi = Regex.Match(path, @"mi_[0-9a-f]{2}", RegexOptions.IgnoreCase);
        var col = Regex.Match(path, @"col[0-9a-f]{2}", RegexOptions.IgnoreCase);
        string s = string.Join("&", new[] { mi.Success ? mi.Value : "", col.Success ? col.Value : "" }.Where(x => x.Length > 0));
        return s.Length > 0 ? s.ToLowerInvariant() : "—";
    }

    private static string Usage(HidDevice d)
    {
        try
        {
            uint u = d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).FirstOrDefault();
            return $"{u >> 16:X4}:{u & 0xFFFF:X4}";
        }
        catch
        {
            return "?";
        }
    }

    private static List<string> LogTail()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) return new List<string>();
            return File.ReadLines(Log.FilePath)
                .Where(l => !l.Contains("layout:") && !l.Contains("attached to taskbar"))
                .TakeLast(25)
                .Select(l => Regex.Replace(l, @"\\\\\?\\hid#[^\s|]+", "<hid-path>", RegexOptions.IgnoreCase))
                .Select(l => Regex.Replace(l, @"[^\s,=(]+[’'`]s\s+", ""))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string product = int.TryParse(build, out int b) && b >= 22000 ? "Windows 11" : "Windows 10";
            return $"{product} {display} (build {build})".Replace("  ", " ");
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private static string Safe(Func<string> f)
    {
        try { return f()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static string Len(Func<int> f)
    {
        try { return f().ToString(); }
        catch { return "?"; }
    }
}
