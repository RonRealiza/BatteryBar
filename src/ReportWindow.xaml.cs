using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BatteryBar;

/// <summary>Окно «Устройство не определилось?»: выбор устройства, комментарий, предпросмотр и отправка отчёта.</summary>
internal partial class ReportWindow : Window
{
    // Ссылки длиннее браузеры и GitHub обрезают — тогда отчёт идёт через буфер обмена.
    private const int MaxUrlLength = 7500;

    private readonly Func<IReadOnlyList<DeviceBattery>> _shown;
    private bool _ready;

    public ReportWindow(Func<IReadOnlyList<DeviceBattery>> shown)
    {
        _shown = shown;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Перечисление устройств и Bluetooth занимает до секунды — не держим окно.
        var candidates = await Task.Run(DeviceReport.Candidates);
        DevicesList.ItemsSource = candidates;
        LoadingText.Visibility = Visibility.Collapsed;
        _ready = true;
        await UpdatePreviewAsync();
    }

    private async void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == DevicesList && DevicesList.SelectedItem is ReportCandidate c && ModelBox.Text.Trim().Length == 0)
            ModelBox.Text = c.Title.Split("  (")[0];
        await UpdatePreviewAsync();
    }

    private async Task UpdatePreviewAsync()
    {
        var selected = DevicesList.SelectedItem as ReportCandidate;
        string comment = CommentBox.Text;
        var shown = _shown();
        string report = await Task.Run(() => DeviceReport.Build(selected, comment, shown));
        PreviewBox.Text = report;
        SendButton.IsEnabled = selected != null && ModelBox.Text.Trim().Length > 0;
        StatusText.Text = selected == null ? "Выберите устройство из списка" : "";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PreviewBox.Text);
        StatusText.Text = "Отчёт скопирован — можно вставить в письмо или сообщение";
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        string model = ModelBox.Text.Trim();
        string title = $"[Устройство] {model}";
        string report = PreviewBox.Text;

        string url = DeviceReport.IssueUrl(title, model, report);
        Log.Write($"report: issue url {url.Length} chars");
        if (url.Length > MaxUrlLength)
        {
            Clipboard.SetText(report);
            url = DeviceReport.IssueUrl(title, model, "Отчёт скопирован в буфер обмена — вставьте его сюда (Ctrl+V).");
            StatusText.Text = "Отчёт длинный и скопирован в буфер — вставьте его в заявку (Ctrl+V)";
        }
        else
        {
            StatusText.Text = "Открываю GitHub — проверьте заявку и нажмите «Create»";
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
