using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBar;

/// <summary>
/// Подсказка у блока на панели задач в стиле Windows 11: скруглённые углы, тонкая рамка и тень — средствами самой
/// Windows (DWM), цвета — светлые или тёмные, как у панели задач. Стандартная подсказка WinForms выглядит как
/// в старых Windows и не знает тёмной темы.
/// </summary>
internal sealed class TaskbarTooltip : Form
{
    private const int CS_DROPSHADOW = 0x00020000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly string FontName = OverlayWindow.PickFontName("Segoe UI Variable Text", "Segoe UI");

    private string _text = "";
    private Font? _font;
    private bool _light;
    private Padding _padding;

    public TaskbarTooltip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            // Мышь сквозь подсказку, фокус не забирает, в Alt+Tab не попадает.
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Показать текст. anchor — точка в экранных координатах: середина края подсказки, обращённого к устройству;
    /// side — с какой стороны от этой точки встать (вверх, вниз, влево, вправо).
    /// </summary>
    public void ShowAt(string text, Point anchor, AnchorStyles side, bool light, int dpi)
    {
        float s = dpi / 96f;
        _text = text;
        _light = light;
        _font?.Dispose();
        _font = new Font(FontName, 12 * s, GraphicsUnit.Pixel);
        _padding = new Padding((int)(9 * s), (int)(6 * s), (int)(9 * s), (int)(7 * s));

        var textSize = TextRenderer.MeasureText(_text, _font, Size.Empty, TextFormatFlags.NoPadding);
        var size = new Size(textSize.Width + _padding.Horizontal, textSize.Height + _padding.Vertical);
        var location = side switch
        {
            AnchorStyles.Top => new Point(anchor.X - size.Width / 2, anchor.Y - size.Height),
            AnchorStyles.Bottom => new Point(anchor.X - size.Width / 2, anchor.Y),
            AnchorStyles.Left => new Point(anchor.X - size.Width, anchor.Y - size.Height / 2),
            _ => new Point(anchor.X, anchor.Y - size.Height / 2),
        };
        // Не вылезать за край экрана.
        var screen = Screen.FromPoint(anchor).Bounds;
        location.X = Math.Clamp(location.X, screen.Left + 4, screen.Right - size.Width - 4);
        location.Y = Math.Clamp(location.Y, screen.Top + 4, screen.Bottom - size.Height - 4);

        BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(43, 43, 43);
        Bounds = new Rectangle(location, size);
        ApplyWindowsStyle();
        Invalidate();
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Скругление и рамка, как у всплывающих окон Windows 11 (на Windows 10 просто не применятся).</summary>
    private void ApplyWindowsStyle()
    {
        int corner = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = _light ? Color.FromArgb(229, 229, 229) : Color.FromArgb(70, 70, 70);
        int colorRef = border.R | border.G << 8 | border.B << 16;
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_font == null) return;
        var fg = _light ? Color.FromArgb(27, 27, 27) : Color.White;
        TextRenderer.DrawText(e.Graphics, _text, _font, new Point(_padding.Left, _padding.Top), fg, TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font?.Dispose();
        base.Dispose(disposing);
    }
}
