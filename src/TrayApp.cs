using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using BatteryBar.Providers;
using Microsoft.Win32;

namespace BatteryBar;

internal sealed class TrayApp : ApplicationContext, ISettingsHost
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BatteryBar";

    // По окну на каждую панель задач, где показываем блок. Ключ — окно панели.
    private readonly Dictionary<IntPtr, OverlayWindow> _overlays = new();
    private readonly SynchronizationContext _ui;
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly DeviceTrayIcons _deviceTray;
    private readonly TrayMenu _menu;
    private SettingsWindow? _settingsWindow;
    private readonly BatteryMonitor _monitor;
    private readonly System.Windows.Forms.Timer _topmostTimer;
    private readonly Thread _layoutThread;
    private volatile bool _stopped;

    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();
    private volatile IReadOnlyList<TaskbarLayout> _layouts = Array.Empty<TaskbarLayout>();

    public TrayApp()
    {
        L.Apply(_settings.Language);

        // Первый контрол ставит WindowsFormsSynchronizationContext для UI-потока.
        using (var probe = new Control()) _ = probe.Handle;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // Окно настроек и меню трея — WPF с темой Fluent (стиль Windows 11, светлая/тёмная по системе).
        // Объект Application нужен для ресурсов темы; цикл сообщений остаётся у WinForms.
        var wpf = System.Windows.Application.Current
                  ?? new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // ThemeMode в .NET 9 помечен как экспериментальный
        wpf.ThemeMode = System.Windows.ThemeMode.System;
#pragma warning restore WPF0001

        var menu = _menu = new TrayMenu();
        menu.Add(() => L.MenuSettings, "", OpenSettings);
        menu.Add(() => L.MenuRefresh, "", () => _monitor!.RefreshNow());
        menu.AddSeparator();
        menu.Add(() => L.MenuExit, "", ExitThread);

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "BatteryBar",
            Visible = true,
        };
        _deviceTray = new DeviceTrayIcons(menu, OpenSettings);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings();
            else if (e.Button == MouseButtons.Right) menu.Show();
        };

        _monitor = new BatteryMonitor(new IBatteryProvider[]
        {
            new LogitechProvider(),
            new RazerProvider(),
            new HyperXProvider(),
            new HeadsetsProvider(),
            new ZornherProvider(),
            new DualSenseProvider(),
            new DualShock4Provider(),
            new XboxProvider(),
            new AirPodsProvider(),
            new BluetoothProvider(),
        });
        _monitor.Updated += devices => _ui.Post(_ =>
        {
            _devices = devices;
            UpdateTooltip();
            Redraw();
            DevicesChanged?.Invoke();
        }, null);

        _topmostTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _topmostTimer.Tick += (_, _) =>
        {
            foreach (var overlay in _overlays.Values) overlay.KeepOnTop();
        };
        _topmostTimer.Start();

        // UI Automation к explorer.exe может подтормаживать — держим его вне UI-потока.
        _layoutThread = new Thread(LayoutLoop) { IsBackground = true, Name = "TaskbarLayout" };
        _layoutThread.SetApartmentState(ApartmentState.MTA);
        _layoutThread.Start();

        _monitor.Start();
        Log.Write("started");
    }

    private void LayoutLoop()
    {
        while (!_stopped)
        {
            IReadOnlyList<TaskbarLayout> layouts = _layouts;
            try
            {
                layouts = TaskbarLocator.LocateAll();
            }
            catch (Exception ex)
            {
                Log.Error("layout", ex);
            }

            if (!layouts.SequenceEqual(_layouts))
            {
                foreach (var l in layouts.Except(_layouts))
                    Log.Write($"layout: {l}");
                _layouts = layouts;
                _ui.Post(_ => Redraw(), null);
            }
            Thread.Sleep(1000);
        }
    }

    private static OverlayWindow CreateOverlay()
    {
        var overlay = new OverlayWindow();
        overlay.CreateControl();
        _ = overlay.Handle;
        return overlay;
    }

    private void Redraw()
    {
        try
        {
            _deviceTray.Update(_settings.ShowInTray ? _devices : Array.Empty<DeviceBattery>());
        }
        catch (Exception ex)
        {
            Log.Error("tray icons", ex);
        }

        var shown = _settings.ShowOnTaskbar ? _settings.Filter(_layouts) : new List<TaskbarLayout>();
        var wanted = shown.Select(l => l.Handle).ToHashSet();

        foreach (var handle in _overlays.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            _overlays[handle].Dispose();
            _overlays.Remove(handle);
        }

        foreach (var layout in shown)
        {
            try
            {
                if (!_overlays.TryGetValue(layout.Handle, out var overlay) || overlay.IsOrphaned || overlay.IsDisposed)
                {
                    // Новая панель задач, или Проводник перезапустился и унёс окно вместе со старой.
                    if (overlay is { IsDisposed: false }) overlay.Dispose();
                    overlay = CreateOverlay();
                    _overlays[layout.Handle] = overlay;
                }
                overlay.Render(_devices, layout);
            }
            catch (Exception ex)
            {
                Log.Error("render", ex);
            }
        }
    }

    /// <summary>Открыть окно настроек, как только запустится цикл сообщений.</summary>
    public void ShowSettingsOnStart() => _ui.Post(_ => OpenSettings(), null);

    /// <summary>Открыть окно «Устройство не определилось?» сразу после запуска (для проверки).</summary>
    public void ShowReportOnStart() => _ui.Post(_ =>
    {
        var window = new ReportWindow(() => _devices);
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        window.Show();
    }, null);

    /// <summary>Открыть меню трея у курсора сразу после запуска (для проверки внешнего вида).</summary>
    public void ShowMenuOnStart() => _ui.Post(_ => _menu.Show(), null);

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                _settingsWindow.WindowState = System.Windows.WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        // Клавиатура в WPF-окне внутри цикла сообщений WinForms.
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settingsWindow);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ISettingsHost

    public event Action? DevicesChanged;

    Settings ISettingsHost.Settings => _settings;

    IReadOnlyList<DeviceBattery> ISettingsHost.Devices => _devices;

    IReadOnlyList<TaskbarLayout> ISettingsHost.Layouts => _layouts;

    bool ISettingsHost.Autostart
    {
        get => IsAutostart();
        set => SetAutostart(value);
    }

    bool ISettingsHost.IsDebugBuild =>
        Environment.ProcessPath?.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) == true;

    void ISettingsHost.ApplySettings()
    {
        _settings.Save();
        Log.Write($"settings: display={_settings.Display} monitors={_settings.Monitors} {string.Join(",", _settings.SelectedMonitors)}");
        Redraw();
    }

    void ISettingsHost.RefreshNow() => _monitor.RefreshNow();

    void ISettingsHost.LanguageChanged()
    {
        _settings.Save();
        L.Apply(_settings.Language);
        Log.Write($"language: {_settings.Language} (english={L.En})");
        UpdateTooltip();
        Redraw();

        // Тексты окна читаются при его создании — переоткрываем на том же месте, уже после текущего события.
        if (_settingsWindow is not { } old) return;
        double left = old.Left, top = old.Top;
        _ui.Post(_ =>
        {
            old.Close();
            OpenSettings();
            if (_settingsWindow is { } fresh)
            {
                fresh.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                fresh.Left = left;
                fresh.Top = top;
            }
        }, null);
    }

    void ISettingsHost.OpenLog() => OpenLog();

    private void UpdateTooltip()
    {
        string text = _devices.Count == 0
            ? L.NoDevicesTooltip
            : string.Join("\n", _devices.Select(d =>
                $"{d.Name}: {(d.Percent is { } p ? p + "%" : "?")}{StateSuffix(d.State)}"));
        // NotifyIcon.Text ограничен 127 символами.
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string StateSuffix(ChargeState state) => state switch
    {
        ChargeState.Charging => L.ChargingSuffix,
        ChargeState.Full => L.FullSuffix,
        _ => "",
    };

    /// <summary>Иконка приложения из ресурсов, в размере маленьких иконок трея для текущего масштаба.</summary>
    private static Icon CreateTrayIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/assets/BatteryBar.ico"));
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static void OpenLog()
    {
        Log.Write("log opened");
        Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _stopped = true;
        _monitor.Dispose();
        _topmostTimer.Dispose();
        _settingsWindow?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _deviceTray.Dispose();
        foreach (var overlay in _overlays.Values) overlay.Close();
        base.ExitThreadCore();
    }
}
