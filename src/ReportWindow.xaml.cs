using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BatteryBar;

/// <summary>
/// Окно «Устройство не определилось?»: сначала только выбор устройства, после выбора —
/// модель, подробности, предпросмотр отчёта и отправка.
/// </summary>
internal partial class ReportWindow : Window
{
    // Ссылки длиннее браузеры и GitHub обрезают — тогда отчёт идёт через буфер обмена.
    private const int MaxUrlLength = 7500;

    private readonly Func<IReadOnlyList<DeviceBattery>> _shown;

    public ReportWindow(Func<IReadOnlyList<DeviceBattery>> shown)
    {
        _shown = shown;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Перечисление устройств и Bluetooth занимает до секунды — не держим окно.
        foreach (var candidate in await Task.Run(DeviceReport.Candidates))
        {
            var check = new CheckBox { Content = candidate.Title, Tag = candidate, Margin = new Thickness(0, 2, 0, 2) };
            check.Checked += DeviceCheck_Changed;
            check.Unchecked += DeviceCheck_Changed;
            DeviceChecks.Children.Add(check);
        }
        ComboHint.Text = L.ReportPick;
    }

    private List<ReportCandidate> Selected() =>
        DeviceChecks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (ReportCandidate)c.Tag).ToList();

    // Модель подставляем из названий отмеченных устройств, пока пользователь не исправил её сам.
    private string _autoModel = "";

    private async void DeviceCheck_Changed(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Count > 0)
        {
            ComboHint.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Visible;
        }
        string auto = string.Join(", ", selected.Select(c => c.Title.Split("  (")[0]).Distinct());
        if (ModelBox.Text == _autoModel) ModelBox.Text = auto;
        _autoModel = auto;
        await UpdatePreviewAsync();
    }

    private async void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (DetailsPanel.Visibility == Visibility.Visible)
            await UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        var selected = Selected();
        string comment = CommentBox.Text;
        var shown = _shown();
        PreviewBox.Text = await Task.Run(() => DeviceReport.Build(selected, comment, shown));
        SendButton.IsEnabled = selected.Count > 0 && ModelBox.Text.Trim().Length > 0;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PreviewBox.Text);
        StatusText.Text = L.ReportCopied;
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        string model = ModelBox.Text.Trim();
        string title = $"{L.ReportIssuePrefix} {model}";
        string report = PreviewBox.Text;

        string url = DeviceReport.IssueUrl(title, model, report);
        Log.Write($"report: issue url {url.Length} chars");
        if (url.Length > MaxUrlLength)
        {
            Clipboard.SetText(report);
            url = DeviceReport.IssueUrl(title, model, L.ReportPasteHere);
            StatusText.Text = L.ReportTooLong;
        }
        else
        {
            StatusText.Text = L.ReportOpening;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
