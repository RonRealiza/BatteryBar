using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BatteryBar;

internal static class Program
{
    /// <summary>Повторный запуск программы: уже работающая копия по этому сигналу открывает настройки.</summary>
    public const string ShowSettingsEvent = @"Local\BatteryBar.ShowSettings";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--list-hid"))
        {
            // Диагностика: выписать все HID-устройства в журнал и выйти.
            Diagnostics.DumpHidDevices();
            return;
        }

        if (args.Length >= 2 && args[0] == "--icons-preview")
        {
            // Для проверки дизайна: все значки устройств в нескольких размерах в PNG.
            DeviceIcons.SavePreview(args[1]);
            return;
        }

        using var mutex = new Mutex(true, @"Local\BatteryBar.SingleInstance", out bool first);
        if (!first)
        {
            // Сразу после обновления старая копия может ещё закрываться — ждём её, а не выходим.
            if (!args.Contains("--updated"))
            {
                SignalRunningInstance();
                return;
            }
            if (!WaitForPreviousInstance(mutex)) return;
        }

        // Портативная версия после обновления оставляет старый exe рядом — удаляем его.
        if (Environment.ProcessPath is { } exe)
        {
            try { System.IO.File.Delete(exe + ".old"); }
            catch { /* ещё занят — удалим при следующем запуске */ }
        }

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

    /// <summary>
    /// Программа уже запущена — просим её показать настройки. Это единственный путь к ним,
    /// когда значок в трее скрыт, а заряд показывается только на панели задач.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            // Окно откроет другой процесс — разрешаем ему выйти на передний план вместо нас.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            using var signal = EventWaitHandle.OpenExisting(ShowSettingsEvent);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // старая версия без сигнала или копия ещё запускается
        }
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true; // старая копия завершилась, не отпустив мьютекс, — он теперь наш
        }
    }
}
