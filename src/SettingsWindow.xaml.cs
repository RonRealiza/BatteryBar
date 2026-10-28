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

    /// <summary>Язык сменился: применить его и переоткрыть окно настроек уже на новом языке.</summary>
    void LanguageChanged();

    Updater Updater { get; }

    /// <summary>Скачать и установить найденное обновление (программа закроется и перезапустится).</summary>
    void InstallUpdate();

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
        LanguageCombo.SelectedIndex = (int)host.Settings.Language;
        AutostartCheck.IsChecked = host.Autostart;
        AutoUpdateCheck.IsChecked = host.Settings.AutoCheckUpdates;
        if (host.IsDebugBuild)
            AutostartHint.Text = L.AutostartDebugHint;
        LoadMonitors();
        _loading = false;

        VersionText.Text = $"BatteryBar {Updater.Current}";
        UpdateDevices();
        UpdateEnabledState();
        UpdateUpdateCard();

        host.DevicesChanged += UpdateDevices;
        host.Updater.Changed += UpdateUpdateCard;
        Closed += (_, _) =>
        {
            host.DevicesChanged -= UpdateDevices;
            host.Updater.Changed -= UpdateUpdateCard;
        };
    }

    /// <summary>Текст, кнопка и красная точка карточки «Обновления» по состоянию проверки.</summary>
    private void UpdateUpdateCard()
    {
        var u = _host.Updater;
        var latest = u.Available?.Version;
        UpdateStatusText.Text = u.State switch
        {
            UpdateState.Checking => L.UpdateChecking,
            UpdateState.UpToDate => L.UpdateUpToDate(Updater.Current),
            UpdateState.Available => L.UpdateAvailable(latest!),
            UpdateState.Downloading => L.UpdateDownloading(u.Progress),
            UpdateState.Installing => L.UpdateInstalling,
            UpdateState.Failed => L.UpdateFailed(u.Error ?? "?"),
            _ => L.UpdateCurrent(Updater.Current),
        };
        UpdateButton.Content = u.State switch
        {
            UpdateState.Available => L.UpdateInstallButton,
            UpdateState.Failed => L.UpdateRetryButton,
            UpdateState.Downloading or UpdateState.Installing => L.UpdateInstallButton,
            _ => L.UpdateCheckButton,
        };
        UpdateButton.IsEnabled = !u.IsBusy;
        bool available = u.Available != null;
        // Акцентная кнопка, когда есть что ставить. Иначе очищаем свойство, а не ставим null:
        // null отключил бы и стиль Fluent из темы, и кнопка стала бы классической серой.
        if (available)
            UpdateButton.Style = (Style)FindResource("AccentButtonStyle");
        else
            UpdateButton.ClearValue(StyleProperty);
        UpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // Есть найденная версия — ставим её (и при «Повторить» после неудачной загрузки), иначе проверяем.
        if (_host.Updater.Available != null)
            _host.InstallUpdate();
        else
            await _host.Updater.CheckAsync();
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Updater.Available is { } info)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
    }

    private void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        _host.ApplySettings();
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
                Content = $"{L.Monitor} {i + 1}{(screen.Primary ? L.PrimarySuffix : "")} — {screen.Bounds.Width}×{screen.Bounds.Height}"
                          + (hasTaskbar ? "" : L.NoTaskbarSuffix),
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

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedIndex < 0) return;
        _host.Settings.Language = (Language)LanguageCombo.SelectedIndex;
        _host.LanguageChanged();
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

    private void ReportDevice_Click(object sender, RoutedEventArgs e)
    {
        var window = new ReportWindow(() => _host.Devices) { Owner = this };
        // Ввод текста в WPF-окне внутри цикла сообщений WinForms.
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        window.Show();
    }

    /// <summary>Заявка «Предложение» на GitHub с уже указанной версией программы.</summary>
    private void SuggestIdea_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        string template = L.En ? "idea-en.yml" : "idea.yml";
        string url = $"{DeviceReport.RepoUrl}/issues/new?template={template}&labels=enhancement" +
                     $"&title={Uri.EscapeDataString(L.IdeaIssuePrefix + " ")}&version={Uri.EscapeDataString(version)}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

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
            Icon = DeviceIcons.Geometry(d);
            Name = d.Badge is { Length: > 0 } b ? $"{d.Name} ({b})" : d.Name;
            PercentText = d.Percent is { } p ? $"{p}%" : "—";
            Status = d.State switch
            {
                ChargeState.Charging => L.Charging,
                ChargeState.Full => L.FullyCharged,
                _ when d.Percent is <= 20 => L.LowBattery,
                _ => L.OnBattery,
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

        public Geometry Icon { get; }
        public Geometry Bolt { get; } = DeviceIcons.BoltGeometry();
        public string Name { get; }
        public string Status { get; }
        public string PercentText { get; }
        public Brush PercentBrush { get; }
        public Brush ChargeBrush { get; }
        public Visibility ChargeVisibility { get; }
    }
}
