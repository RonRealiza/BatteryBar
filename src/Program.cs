using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BatteryBar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--list-hid"))
        {
            // Диагностика: выписать все HID-устройства в журнал и выйти.
            Diagnostics.DumpHidDevices();
            return;
        }

        using var mutex = new Mutex(true, @"Local\BatteryBar.SingleInstance", out bool first);
        if (!first) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"[fatal] {e.ExceptionObject}");

        var app = new TrayApp();
        if (args.Contains("--settings"))
            app.ShowSettingsOnStart();
        if (args.Contains("--report"))
            app.ShowReportOnStart();
        if (args.Contains("--menu"))
            app.ShowMenuOnStart();
        Application.Run(app);
    }
}
