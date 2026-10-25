using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace BatteryBar;

/// <summary>
/// Контекстное меню иконок в трее в стиле меню Windows 11 (как у значка сети): скруглённые углы,
/// значки Segoe Fluent Icons у пунктов, подсветка при наведении, цвета текущей темы.
/// Это WPF ContextMenu с темой Fluent из .NET 9 (тема включается для всего приложения в TrayApp).
/// </summary>
internal sealed class TrayMenu
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly List<(Func<string>? Text, string? Glyph, Action? Action, Func<bool>? Visible)> _items = new();
    private ContextMenu? _open;

    /// <param name="text">Текст пункта; читается при каждом открытии, чтобы смена языка применялась сразу.</param>
    /// <param name="glyph">Значок из Segoe Fluent Icons, например "" — шестерёнка.</param>
    /// <param name="visible">Показывать ли пункт (проверяется при каждом открытии); null — всегда.</param>
    public void Add(Func<string> text, string glyph, Action action, Func<bool>? visible = null) =>
        _items.Add((text, glyph, action, visible));

    public void AddSeparator(Func<bool>? visible = null) => _items.Add((null, null, null, visible));

    /// <summary>Показать меню у курсора.</summary>
    public void Show()
    {
        _open?.SetCurrentValue(ContextMenu.IsOpenProperty, false);

        // Размеры подогнаны под системные меню Windows 11 (например, у значка сети).
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 220 };
        foreach (var (text, glyph, action, visible) in _items)
        {
            if (visible != null && !visible()) continue;
            if (text == null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem
            {
                Header = text(),
                Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16, Margin = new Thickness(0, 0, 4, 0) },
                Padding = new Thickness(4, 2, 16, 2),
            };
            item.Click += (_, _) => action!();
            menu.Items.Add(item);
        }

        // Во время клика по значку фокус у панели задач, и меню без фокуса сразу закрывается.
        // Поэтому сначала выводим на передний план своё невидимое окно 1×1 под курсором и открываем меню от него.
        var host = Host();
        var pos = System.Windows.Forms.Cursor.Position;
        double scale = VisualTreeHelper.GetDpi(host).DpiScaleX;
        host.Left = pos.X / scale;
        host.Top = pos.Y / scale;
        host.Show();
        host.Activate();
        if (PresentationSource.FromVisual(host) is HwndSource source)
            Native.SetForegroundWindow(source.Handle);

        menu.PlacementTarget = host;
        menu.Closed += (_, _) =>
        {
            _open = null;
            host.Hide();
        };
        _open = menu;
        // Открываем, когда Windows закончит обрабатывать сам клик по значку.
        host.Dispatcher.BeginInvoke(() => menu.IsOpen = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    private Window? _host;

    private Window Host() => _host ??= new Window
    {
        Width = 1,
        Height = 1,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ShowInTaskbar = false,
        Topmost = true,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };
}
