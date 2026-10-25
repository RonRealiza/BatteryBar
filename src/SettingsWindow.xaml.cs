using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace BatteryBar;

/// <summary>Что окно настроек берёт у приложения и что может в нём менять.</summary>
internal interface ISettingsHost
{
    Settings Settings { get; }
    IReadOnlyList<DeviceBattery> Devices { get; }
    IReadOnlyList<TaskbarLayout> Layouts { get; }
    bool Autostart { get; set; }
    bool IsDebugBuild { get; }

    /// <summary>Сохранить настройки и перерисовать.</summary>
    void ApplySettings();

    void RefreshNow();
    void OpenLog();

    event Action DevicesChanged;
}

internal partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private bool _loading;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
#pragma warning disable WPF0001 // ThemeMode в .NET 9 помечен как экспериментальный
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        InitializeComponent();

        _loading = true;
        DisplayCombo.SelectedIndex = (int)host.Settings.Display;
        AutostartCheck.IsChecked = host.Autostart;
        if (host.IsDebugBuild)
            AutostartHint.Text = "Сейчас запущена отладочная сборка — автозапуск будет указывать на неё";
        LoadMonitors();
        _loading = false;

        VersionText.Text = $"BatteryBar {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        UpdateDevices();
        UpdateEnabledState();

        host.DevicesChanged += UpdateDevices;
        Closed += (_, _) => host.DevicesChanged -= UpdateDevices;
    }

    private void UpdateDevices()
    {
        bool light = AppsUseLightTheme();
        var rows = _host.Devices.Select(d => new DeviceRow(d, light, this)).ToList();
        DevicesList.ItemsSource = rows;
        NoDevicesCard.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadMonitors()
    {
        var s = _host.Settings;
        PrimaryRadio.IsChecked = s.Monitors == MonitorMode.Primary;
        AllRadio.IsChecked = s.Monitors == MonitorMode.All;
        SelectedRadio.IsChecked = s.Monitors == MonitorMode.Selected;

        MonitorChecks.Children.Clear();
        var screens = Screen.AllScreens.OrderBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToList();
        for (int i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            bool hasTaskbar = _host.Layouts.Any(l => l.Monitor == screen.DeviceName);
            var check = new CheckBox
            {
                Content = $"Монитор {i + 1}{(screen.Primary ? " (главный)" : "")} — {screen.Bounds.Width}×{screen.Bounds.Height}"
                          + (hasTaskbar ? "" : " — нет панели задач"),
                Tag = screen.DeviceName,
                IsChecked = s.SelectedMonitors.Contains(screen.DeviceName),
                IsEnabled = hasTaskbar,
                Margin = new Thickness(0, 2, 0, 2),
            };
            check.Checked += MonitorCheck_Changed;
            check.Unchecked += MonitorCheck_Changed;
            MonitorChecks.Children.Add(check);
        }
    }

    private void UpdateEnabledState()
    {
        MonitorsCard.IsEnabled = _host.Settings.ShowOnTaskbar;
        MonitorsCard.Opacity = MonitorsCard.IsEnabled ? 1 : 0.5;
        MonitorChecks.IsEnabled = _host.Settings.Monitors == MonitorMode.Selected;
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedIndex < 0) return;
        _host.Settings.Display = (DisplayMode)DisplayCombo.SelectedIndex;
        _host.ApplySettings();
        UpdateEnabledState();
    }

    private void MonitorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _host.Settings;
        s.Monitors = sender == AllRadio ? MonitorMode.All
            : sender == SelectedRadio ? MonitorMode.Selected
            : MonitorMode.Primary;

        // Впервые выбрали «Выбранные» — отмечаем то, что показывается сейчас, чтобы блок не пропал.
        if (s.Monitors == MonitorMode.Selected && s.SelectedMonitors.Count == 0)
        {
            s.SelectedMonitors = _host.Layouts.Where(l => l.IsPrimary).Select(l => l.Monitor).ToList();
            _loading = true;
            foreach (var check in MonitorChecks.Children.OfType<CheckBox>())
                check.IsChecked = s.SelectedMonitors.Contains((string)check.Tag);
            _loading = false;
        }
        _host.ApplySettings();
        UpdateEnabledState();
    }

    private void MonitorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.SelectedMonitors = MonitorChecks.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        _host.ApplySettings();
    }

    private void AutostartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Autostart = AutostartCheck.IsChecked == true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _host.RefreshNow();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => _host.OpenLog();

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Строка списка устройств.</summary>
    private sealed class DeviceRow
    {
        public DeviceRow(DeviceBattery d, bool light, FrameworkElement owner)
        {
            Glyph = d.Kind switch
            {
                DeviceKind.Mouse => "",
                DeviceKind.Keyboard => "",
                DeviceKind.Gamepad => "",
                DeviceKind.Headset => "",
                _ => "",
            };
            Name = d.Badge is { Length: > 0 } b ? $"{d.Name} ({b})" : d.Name;
            PercentText = d.Percent is { } p ? $"{p}%" : "—";
            Status = d.State switch
            {
                ChargeState.Charging => "Заряжается",
                ChargeState.Full => "Полностью заряжено",
                _ when d.Percent is <= 20 => "Низкий заряд",
                _ => "Работает от батареи",
            };

            bool low = d.Percent is <= 20 && d.State != ChargeState.Charging;
            var critical = light ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x99, 0xA4);
            var success = light ? Color.FromRgb(0x0F, 0x7B, 0x0F) : Color.FromRgb(0x6C, 0xCB, 0x5F);
            PercentBrush = low
                ? new SolidColorBrush(critical)
                : owner.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? (light ? Brushes.Black : Brushes.White);
            ChargeBrush = new SolidColorBrush(success);
            ChargeVisibility = d.State == ChargeState.Charging ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Glyph { get; }
        public string Name { get; }
        public string Status { get; }
        public string PercentText { get; }
        public Brush PercentBrush { get; }
        public Brush ChargeBrush { get; }
        public Visibility ChargeVisibility { get; }
    }
}
