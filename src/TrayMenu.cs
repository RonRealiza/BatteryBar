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

    private readonly List<(string? Text, string? Glyph, Action? Action)> _items = new();
    private ContextMenu? _open;

    /// <param name="glyph">Значок из Segoe Fluent Icons, например "" — шестерёнка.</param>
    public void Add(string text, string glyph, Action action) => _items.Add((text, glyph, action));

    public void AddSeparator() => _items.Add((null, null, null));

    /// <summary>Показать меню у курсора.</summary>
    public void Show()
    {
        _open?.SetCurrentValue(ContextMenu.IsOpenProperty, false);

        // Размеры подогнаны под системные меню Windows 11 (например, у значка сети).
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, MinWidth = 220 };
        foreach (var (text, glyph, action) in _items)
        {
            if (text == null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem
            {
                Header = text,
                Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 16, Margin = new Thickness(0, 0, 4, 0) },
                Padding = new Thickness(4, 2, 16, 2),
            };
            item.Click += (_, _) => action!();
            menu.Items.Add(item);
        }

        // Наше приложение не на переднем плане — без этого меню не получит фокус и не закроется кликом мимо.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
                Native.SetForegroundWindow(source.Handle);
            menu.Focus();
        };
        menu.Closed += (_, _) => _open = null;
        _open = menu;
        menu.IsOpen = true;
    }
}
