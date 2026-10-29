using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
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

    /// <summary>Перерисовать по текущим настройкам, не сохраняя их (пока тянут ползунок).</summary>
    void Preview();

    /// <summary>Пользователь перетащил устройства: новый порядок их Id.</summary>
    void SetDeviceOrder(IReadOnlyList<string> ids);

    /// <summary>Своё название устройства; пустое — вернуть исходное.</summary>
    void RenameDevice(string id, string? name);

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
        SeparatorCombo.SelectedIndex = (int)host.Settings.Separator;
        PositionCombo.SelectedIndex = (int)host.Settings.Position;
        UpdateOffsetRow();
        LanguageCombo.SelectedIndex = (int)host.Settings.Language;
        AutostartCheck.IsChecked = host.Autostart;
        AutoUpdateCheck.IsChecked = host.Settings.AutoCheckUpdates;
        HideIconCheck.IsChecked = host.Settings.HideAppIcon;
        if (host.IsDebugBuild)
            AutostartHint.Text = L.AutostartDebugHint;
        LoadMonitors();
        _loading = false;

        VersionText.Text = $"BatteryBar {Updater.Current}";
        UpdateDevices();
        UpdateEnabledState();
        UpdateUpdateCard();

        host.DevicesChanged += UpdateDevices;
        _saveOffset.Tick += (_, _) =>
        {
            _saveOffset.Stop();
            _host.ApplySettings();
        };
        _refreshAfterDrag.Tick += (_, _) =>
        {
            _refreshAfterDrag.Stop();
            UpdateDevices();
        };
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
        // Под мышью или пока строка садится на место список не перестраиваем — обновим чуть позже.
        if (Mouse.LeftButton != MouseButtonState.Pressed) _pressRow = null; // отпустили за пределами списка
        if (_drag != null || _pressRow != null || _editing != null || DateTime.UtcNow < _settleUntil)
        {
            _refreshAfterDrag.Stop();
            _refreshAfterDrag.Start();
            return;
        }
        bool light = AppsUseLightTheme();
        _rows = new ObservableCollection<DeviceRow>(_host.Devices.Select(d => new DeviceRow(d, light, this)));
        DevicesList.ItemsSource = _rows;
        NoDevicesCard.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Перетаскивание строк устройств. Схваченная строка приподнимается (чуть крупнее, с тенью) и едет за мышью,
    // остальные плавно уступают ей место; после отпускания она мягко садится в новую ячейку.
    private ObservableCollection<DeviceRow> _rows = new();
    private DeviceRow? _pressRow;
    private Point _pressAt;
    private DragState? _drag;
    private DateTime _settleUntil;
    private readonly System.Windows.Threading.DispatcherTimer _refreshAfterDrag = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private static readonly Duration Quick = new(TimeSpan.FromMilliseconds(180));
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Ячейки строк на момент захвата: верх и высота каждой (с отступом до следующей).</summary>
    private sealed class DragState
    {
        public required int From;
        public required FrameworkElement[] Cells;
        public required double[] Tops;
        public required double[] Heights;
        public required double StartY;
        public int To;
        public double Offset;
    }

    private static TranslateTransform Shift(FrameworkElement cell) => (TranslateTransform)((TransformGroup)cell.RenderTransform).Children[1];
    private static ScaleTransform Lift(FrameworkElement cell) => (ScaleTransform)((TransformGroup)cell.RenderTransform).Children[0];

    private static void Animate(Animatable target, DependencyProperty property, double to, Duration? duration = null) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration ?? Quick) { EasingFunction = EaseOut });

    private void DevicesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Схватить строку можно только за ручку справа.
        _pressRow = null;
        for (var el = e.OriginalSource as DependencyObject; el != null && el != DevicesList; el = VisualTreeHelper.GetParent(el))
        {
            if (el is FrameworkElement { Tag: "DragHandle" } handle)
            {
                _pressRow = handle.DataContext as DeviceRow;
                break;
            }
        }
        _pressAt = e.GetPosition(DevicesList);
    }

    private void DevicesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(DevicesList);
        if (_drag == null)
        {
            if (Math.Abs(at.Y - _pressAt.Y) < SystemParameters.MinimumVerticalDragDistance || _rows.Count < 2) return;
            StartDrag();
            if (_drag == null) return;
        }

        var d = _drag;
        double minOffset = d.Tops[0] - d.Tops[d.From];
        double maxOffset = d.Tops[^1] + d.Heights[^1] - (d.Tops[d.From] + d.Heights[d.From]);
        d.Offset = Math.Clamp(at.Y - d.StartY, minOffset, maxOffset);
        Shift(d.Cells[d.From]).BeginAnimation(TranslateTransform.YProperty, null);
        Shift(d.Cells[d.From]).Y = d.Offset;

        // Соседняя строка уступает место, как только край схваченной заходит за её середину.
        double top = d.Tops[d.From] + d.Offset, bottom = top + d.Heights[d.From];
        int to = d.From;
        for (int i = 0; i < d.Cells.Length; i++)
        {
            double middle = d.Tops[i] + d.Heights[i] / 2;
            if (i > d.From && bottom > middle) to++;
            if (i < d.From && top < middle) to--;
        }
        if (to == d.To) return;
        d.To = to;

        // Строки между старым и новым местом сдвигаются на высоту схваченной, остальные возвращаются.
        for (int i = 0; i < d.Cells.Length; i++)
        {
            if (i == d.From) continue;
            double shift = d.From < to && i > d.From && i <= to ? -d.Heights[d.From]
                         : d.From > to && i >= to && i < d.From ? d.Heights[d.From]
                         : 0;
            Animate(Shift(d.Cells[i]), TranslateTransform.YProperty, shift);
        }
    }

    private void StartDrag()
    {
        // Нажали на строку старого списка (он перестраивается после каждой перестановки) — ищем её по Id в текущем.
        int from = _rows.Select(r => r.Id).ToList().IndexOf(_pressRow!.Id);
        if (from < 0)
        {
            _pressRow = null;
            return;
        }
        var cells = new FrameworkElement[_rows.Count];
        for (int i = 0; i < _rows.Count; i++)
        {
            if (DevicesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) return;
            cells[i] = c;
        }
        var tops = cells.Select(c => c.TranslatePoint(new Point(0, 0), DevicesList).Y).ToArray();
        // Высота ячейки — до верха следующей, чтобы учесть отступ между карточками.
        var heights = cells.Select((c, i) => i + 1 < cells.Length ? tops[i + 1] - tops[i] : c.ActualHeight).ToArray();
        foreach (var c in cells)
        {
            c.RenderTransformOrigin = new Point(0.5, 0.5);
            c.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        }
        _drag = new DragState { From = from, To = from, Cells = cells, Tops = tops, Heights = heights, StartY = _pressAt.Y };

        // Подъём: поверх соседей, чуть крупнее, с мягкой тенью и непрозрачным фоном (карточки полупрозрачные).
        var cell = cells[from];
        Panel.SetZIndex(cell, 1);
        if (VisualTreeHelper.GetChildrenCount(cell) > 0 && VisualTreeHelper.GetChild(cell, 0) is Border card)
            card.Background = (TryFindResource("SolidBackgroundFillColorTertiaryBrush") ?? TryFindResource("SolidBackgroundFillColorBaseBrush")) as Brush
                              ?? card.Background;
        var shadow = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0, Color = Colors.Black };
        cell.Effect = shadow;
        Animate(shadow, DropShadowEffect.OpacityProperty, 0.35);
        Animate(Lift(cell), ScaleTransform.ScaleXProperty, 1.02);
        Animate(Lift(cell), ScaleTransform.ScaleYProperty, 1.02);

        DevicesList.CaptureMouse();
    }

    // Переименование: щелчок по названию — поле ввода на его месте. Enter или уход фокуса — сохранить, Esc — отменить.
    private TextBox? _editing;

    private void Name_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement view || view.DataContext is not DeviceRow row) return;
        if (VisualTreeHelper.GetParent(view) is not Grid cell || cell.Children.OfType<TextBox>().FirstOrDefault() is not { } box) return;
        view.Visibility = Visibility.Collapsed;
        box.Text = row.RawName;
        box.Tag = view;
        box.Visibility = Visibility.Visible;
        _editing = box;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FinishRename((TextBox)sender, save: true);
        else if (e.Key == Key.Escape) FinishRename((TextBox)sender, save: false);
        else return;
        e.Handled = true;
    }

    private void NameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename((TextBox)sender, save: true);

    private void FinishRename(TextBox box, bool save)
    {
        if (_editing != box) return; // уже закончили (Enter, а потом потеря фокуса)
        _editing = null;
        box.Visibility = Visibility.Collapsed;
        if (box.Tag is FrameworkElement view) view.Visibility = Visibility.Visible;
        if (save && box.DataContext is DeviceRow row && box.Text.Trim() != row.RawName)
            _host.RenameDevice(row.Id, box.Text);
        else
            UpdateDevices(); // обновления, отложенные на время ввода
    }

    // Захват мыши отобрали (Alt+Tab, всплывающее окно) — строка садится туда, где сейчас.
    private void DevicesList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag != null) DevicesList_PreviewMouseLeftButtonUp(sender, null!);
    }

    private void DevicesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pressRow = null;
        if (_drag is not { } d) return;
        _drag = null;
        DevicesList.ReleaseMouseCapture();

        // Где строка видна сейчас и где окажется после перестановки — разницу проезжает анимацией.
        double visualTop = d.Tops[d.From] + d.Offset;
        double newTop = d.To > d.From ? d.Tops[d.To] + d.Heights[d.To] - d.Heights[d.From] : d.Tops[d.To];
        var row = _rows[d.From];
        foreach (var c in d.Cells)
        {
            Shift(c).BeginAnimation(TranslateTransform.YProperty, null);
            Shift(c).Y = 0;
        }
        if (d.To != d.From) _rows.Move(d.From, d.To);
        DevicesList.UpdateLayout();

        if (DevicesList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement cell)
        {
            if (cell.RenderTransform is not TransformGroup)
            {
                cell.RenderTransformOrigin = new Point(0.5, 0.5);
                cell.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.02, 1.02), new TranslateTransform() } };
            }
            Shift(cell).Y = visualTop - newTop;
            var settle = new Duration(TimeSpan.FromMilliseconds(220));
            Animate(Shift(cell), TranslateTransform.YProperty, 0, settle);
            Animate(Lift(cell), ScaleTransform.ScaleXProperty, 1, settle);
            Animate(Lift(cell), ScaleTransform.ScaleYProperty, 1, settle);
            if (cell.Effect is DropShadowEffect shadow)
            {
                var fade = new DoubleAnimation(0, settle) { EasingFunction = EaseOut };
                fade.Completed += (_, _) =>
                {
                    cell.Effect = null;
                    Panel.SetZIndex(cell, 0);
                    if (VisualTreeHelper.GetChildrenCount(cell) > 0 && VisualTreeHelper.GetChild(cell, 0) is Border card)
                        card.ClearValue(Border.BackgroundProperty);
                };
                shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
            }
        }

        _settleUntil = DateTime.UtcNow.AddMilliseconds(300);
        if (d.To != d.From)
            _host.SetDeviceOrder(_rows.Select(r => r.Id).ToList());
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
        // Разделитель есть только у блока на панели задач.
        SeparatorCard.IsEnabled = _host.Settings.ShowOnTaskbar;
        SeparatorCard.Opacity = SeparatorCard.IsEnabled ? 1 : 0.5;
        PositionCard.IsEnabled = _host.Settings.ShowOnTaskbar;
        PositionCard.Opacity = PositionCard.IsEnabled ? 1 : 0.5;
        MonitorChecks.IsEnabled = _host.Settings.Monitors == MonitorMode.Selected;
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedIndex < 0) return;
        _host.Settings.Display = (DisplayMode)DisplayCombo.SelectedIndex;
        _host.ApplySettings();
        UpdateEnabledState();
    }

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PositionCombo.SelectedIndex < 0) return;
        _host.Settings.Position = (TaskbarPosition)PositionCombo.SelectedIndex;
        _host.ApplySettings();
        UpdateOffsetRow();
    }

    /// <summary>Ползунок отступа: от погоды или от трея, у остальных мест его нет.</summary>
    private void UpdateOffsetRow()
    {
        var position = _host.Settings.Position;
        bool shown = position is TaskbarPosition.AfterWidgets or TaskbarPosition.BeforeTray;
        OffsetRow.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        OffsetLabel.Text = position == TaskbarPosition.AfterWidgets ? L.OffsetFromWidgets : L.OffsetFromTray;
        bool loading = _loading;
        _loading = true;
        OffsetSlider.Value = _host.Settings.EdgeOffset;
        _loading = loading;
        OffsetValue.Text = $"{_host.Settings.EdgeOffset} px";
    }

    // Пока ползунок тянут — только перерисовка; сохраняем, когда его оставили в покое.
    private readonly System.Windows.Threading.DispatcherTimer _saveOffset = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OffsetValue == null) return; // ещё строится окно
        int value = (int)Math.Round(e.NewValue);
        OffsetValue.Text = $"{value} px";
        if (_loading) return;
        if (_host.Settings.Position == TaskbarPosition.AfterWidgets) _host.Settings.OffsetFromWidgets = value;
        else if (_host.Settings.Position == TaskbarPosition.BeforeTray) _host.Settings.OffsetFromTray = value;
        _host.Preview();
        _saveOffset.Stop();
        _saveOffset.Start();
    }

    private void SeparatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SeparatorCombo.SelectedIndex < 0) return;
        _host.Settings.Separator = (SeparatorStyle)SeparatorCombo.SelectedIndex;
        _host.ApplySettings();
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

    private void HideIconCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.HideAppIcon = HideIconCheck.IsChecked == true;
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
            Id = d.Id;
            RawName = d.Name;
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

        public string Id { get; }

        /// <summary>Название без подписи PS/XB — его и редактируем.</summary>
        public string RawName { get; }

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
