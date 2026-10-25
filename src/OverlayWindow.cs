using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace BatteryBar;

/// <summary>
/// Прозрачное дочернее окно панели задач. Рисуется через UpdateLayeredWindow (попиксельная прозрачность),
/// мышь проходит насквозь, фокус не забирает. Будучи дочерним, оно всегда в одном слое с панелью —
/// не прячется под неё при клике и при открытом «Пуске».
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const char GlyphMouse = '';
    private const char GlyphKeyboard = '';
    private const char GlyphGamepad = '';
    private const char GlyphHeadset = '';
    private const char GlyphOther = '';
    private const char GlyphBolt = '';

    internal static readonly string IconFontName = PickFont("Segoe Fluent Icons", "Segoe MDL2 Assets");
    private static readonly string TextFontName = PickFont("Segoe UI Variable Text", "Segoe UI");

    private bool _visibleOnScreen;
    private IntPtr _parent;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "BatteryBar";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW
                          | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Родительская панель задач уничтожена (перезапуск Проводника) — вместе с ней пропало и это окно.</summary>
    public bool IsOrphaned => _parent != IntPtr.Zero && !Native.IsWindow(_parent);

    /// <summary>XAML-содержимое панели может перекрыть нас среди соседних окон — поднимаемся наверх.</summary>
    public void KeepOnTop()
    {
        if (_visibleOnScreen)
            Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public void Render(IReadOnlyList<DeviceBattery> devices, TaskbarLayout? layout)
    {
        if (layout == null || devices.Count == 0 || layout.SlotEnd - layout.SlotStart < 20)
        {
            SetHidden();
            return;
        }

        float s = layout.Dpi / 96f;
        // На вертикальной панели ширина всего ~48 px — текст чуть мельче, чтобы «100%» поместилось.
        using var iconFont = new Font(IconFontName, 15 * s, GraphicsUnit.Pixel);
        using var boltFont = new Font(IconFontName, (layout.Vertical ? 8.5f : 10) * s, GraphicsUnit.Pixel);
        using var textFont = new Font(TextFontName, (layout.Vertical ? 11 : 12.5f) * s, GraphicsUnit.Pixel);
        using var badgeFont = new Font(TextFontName, (layout.Vertical ? 8.5f : 9.5f) * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var fmt = StringFormat.GenericTypographic;

        var measured = new List<Item>();
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float Width(string text, Font font) => pg.MeasureString(text, font, PointF.Empty, fmt).Width;
            foreach (var d in devices)
            {
                string text = d.Percent is { } p ? $"{p}%" : "—";
                measured.Add(new Item(
                    d,
                    text,
                    Width(Glyph(d.Kind).ToString(), iconFont),
                    d.Badge is { Length: > 0 } badge ? Width(badge, badgeFont) + 2 * s : 0,
                    Width(text, textFont),
                    d.State == ChargeState.Charging ? Width(GlyphBolt.ToString(), boltFont) + 1 * s : 0));
            }
        }

        var colors = new Palette(layout.LightTheme);
        var fonts = new Fonts(iconFont, boltFont, textFont, badgeFont, fmt);
        if (layout.Vertical)
            RenderVertical(measured, layout, s, colors, fonts);
        else
            RenderHorizontal(measured, layout, s, colors, fonts);
    }

    private sealed record Item(DeviceBattery Dev, string Text, float IconW, float BadgeW, float TextW, float BoltW)
    {
        public bool IsLow => Dev.Percent is <= 20 && Dev.State != ChargeState.Charging;
    }

    private sealed record Fonts(Font Icon, Font Bolt, Font Text, Font Badge, StringFormat Format);

    private sealed record Palette(bool Light)
    {
        public Color Fg => Light ? Color.FromArgb(235, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        public Color Low => Light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 153, 164);
        public Color Charge => Light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95);
    }

    /// <summary>Горизонтальная панель: устройства в строку — значок, подпись, процент, молния.</summary>
    private void RenderHorizontal(List<Item> measured, TaskbarLayout layout, float s, Palette c, Fonts f)
    {
        int height = layout.Taskbar.Height;
        int slot = layout.SlotEnd - layout.SlotStart;
        float pad = 2 * s;
        float avail = slot - 2 * pad;
        var widths = measured
            .Select(it => it.IconW + (it.BadgeW > 0 ? it.BadgeW + 1 * s : 0) + 5 * s + it.TextW + it.BoltW)
            .ToList();

        // Сколько устройств влезает при данном отступе в данную длину.
        (int Count, float Used) Fit(float gap, float room)
        {
            int n = 0;
            float used = 0;
            foreach (float w in widths)
            {
                float next = used + (n > 0 ? gap : 0) + w;
                if (next > room) break;
                used = next;
                n++;
            }
            return (n, used);
        }

        // Не влезают все — показываем сколько поместится и метку «+N» с числом оставшихся.
        float gap = 14 * s;
        var (count, usedLen) = Fit(gap, avail);
        int hidden = 0;
        string more = "";
        float moreW = 0;
        if (count < measured.Count)
        {
            more = $"+{measured.Count}"; // самый длинный вариант, чтобы место точно хватило
            using (var probe = new Bitmap(1, 1))
            using (var pg = Graphics.FromImage(probe))
                moreW = pg.MeasureString(more, f.Text, PointF.Empty, f.Format).Width;
            (count, usedLen) = Fit(gap, avail - gap - moreW);
            hidden = measured.Count - count;
            more = $"+{hidden}";
        }
        if (count == 0)
        {
            SetHidden();
            return;
        }

        var items = measured.Take(count).Zip(widths).ToList();
        float used = usedLen + (hidden > 0 ? gap + moreW : 0);
        int width = (int)Math.Ceiling(used + pad * 2);
        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = NewCanvas(bmp))
        {
            float x = pad;
            foreach (var (it, w) in items)
            {
                using var brush = new SolidBrush(it.IsLow ? c.Low : c.Fg);
                DrawCentered(g, Glyph(it.Dev.Kind).ToString(), f.Icon, brush, x, height, f.Format);
                float cx = x + it.IconW;
                if (it.BadgeW > 0)
                {
                    using var badgeBrush = new SolidBrush(Color.FromArgb(190, it.IsLow ? c.Low : c.Fg));
                    DrawCentered(g, it.Dev.Badge!, f.Badge, badgeBrush, cx + 3 * s, height, f.Format);
                    cx += it.BadgeW + 1 * s;
                }
                cx += 5 * s;
                DrawCentered(g, it.Text, f.Text, brush, cx, height, f.Format);
                if (it.BoltW > 0)
                {
                    using var boltBrush = new SolidBrush(c.Charge);
                    DrawCentered(g, GlyphBolt.ToString(), f.Bolt, boltBrush, cx + it.TextW + 1 * s, height, f.Format);
                }
                x += w + gap;
            }

            if (hidden > 0)
            {
                using var moreBrush = new SolidBrush(Color.FromArgb(190, c.Fg));
                DrawCentered(g, more, f.Text, moreBrush, x, height, f.Format);
            }
        }

        int left = layout.EndAligned ? layout.SlotEnd - width : layout.SlotStart;
        Present(bmp, left - layout.Taskbar.Left, 0, layout.Handle);
    }

    /// <summary>Вертикальная панель (сбоку): устройства столбиком, у каждого две строки — значок с подписью и процент.</summary>
    private void RenderVertical(List<Item> measured, TaskbarLayout layout, float s, Palette c, Fonts f)
    {
        int width = layout.Taskbar.Width;
        int slot = layout.SlotEnd - layout.SlotStart;
        float iconRow = 18 * s, textRow = 15 * s, pad = 2 * s, moreRow = 14 * s;
        float itemH = iconRow + textRow;
        float avail = slot - 2 * pad;

        // Сколько устройств влезает при данном отступе в данную длину.
        (int Count, float Used) Fit(float gap, float room)
        {
            int n = 0;
            float used = 0;
            foreach (var _ in measured)
            {
                float next = used + (n > 0 ? gap : 0) + itemH;
                if (next > room) break;
                used = next;
                n++;
            }
            return (n, used);
        }

        // Не влезают все — показываем сколько поместится и метку «+N» с числом оставшихся.
        float gap = 10 * s;
        var (count, usedLen) = Fit(gap, avail);
        int hidden = 0;
        if (count < measured.Count)
        {
            (count, usedLen) = Fit(gap, avail - gap - moreRow);
            hidden = measured.Count - count;
        }
        if (count == 0)
        {
            SetHidden();
            return;
        }

        float used = usedLen + (hidden > 0 ? gap + moreRow : 0);
        int height = (int)Math.Ceiling(used + pad * 2);
        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = NewCanvas(bmp))
        {
            float y = pad;
            foreach (var it in measured.Take(count))
            {
                using var brush = new SolidBrush(it.IsLow ? c.Low : c.Fg);

                // Строка 1: значок (+ подпись PS/XB), по центру панели.
                float row1 = it.IconW + it.BadgeW;
                float x1 = (width - row1) / 2f;
                DrawInRow(g, Glyph(it.Dev.Kind).ToString(), f.Icon, brush, x1, y, iconRow, f.Format);
                if (it.BadgeW > 0)
                {
                    using var badgeBrush = new SolidBrush(Color.FromArgb(190, it.IsLow ? c.Low : c.Fg));
                    DrawInRow(g, it.Dev.Badge!, f.Badge, badgeBrush, x1 + it.IconW + 2 * s, y, iconRow, f.Format);
                }

                // Строка 2: процент (+ молния), по центру панели.
                float row2 = it.TextW + it.BoltW;
                float x2 = (width - row2) / 2f;
                DrawInRow(g, it.Text, f.Text, brush, x2, y + iconRow, textRow, f.Format);
                if (it.BoltW > 0)
                {
                    using var boltBrush = new SolidBrush(c.Charge);
                    DrawInRow(g, GlyphBolt.ToString(), f.Bolt, boltBrush, x2 + it.TextW + 1 * s, y + iconRow, textRow, f.Format);
                }
                y += itemH + gap;
            }

            if (hidden > 0)
            {
                string more = $"+{hidden}";
                using var moreBrush = new SolidBrush(Color.FromArgb(190, c.Fg));
                float mw = g.MeasureString(more, f.Text, PointF.Empty, f.Format).Width;
                DrawInRow(g, more, f.Text, moreBrush, (width - mw) / 2f, y, moreRow, f.Format);
            }
        }

        int top = layout.EndAligned ? layout.SlotEnd - height : layout.SlotStart;
        Present(bmp, 0, top - layout.Taskbar.Top, layout.Handle);
    }

    private static Graphics NewCanvas(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    /// <summary>Текст, выровненный по вертикали внутри строки высотой rowHeight, начиная с y.</summary>
    private static void DrawInRow(Graphics g, string text, Font font, Brush brush, float x, float y, float rowHeight, StringFormat fmt)
    {
        var size = g.MeasureString(text, font, PointF.Empty, fmt);
        g.DrawString(text, font, brush, x, y + (rowHeight - size.Height) / 2f, fmt);
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, float x, int height, StringFormat fmt)
    {
        var size = g.MeasureString(text, font, PointF.Empty, fmt);
        g.DrawString(text, font, brush, x, (height - size.Height) / 2f, fmt);
    }

    /// <summary>x, y — в координатах панели задач.</summary>
    private void Present(Bitmap bmp, int x, int y, IntPtr taskbar)
    {
        AttachTo(taskbar);
        Native.SetWindowPos(Handle, Native.HWND_TOP, x, y, bmp.Width, bmp.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(memDc, hBitmap);
        try
        {
            var size = new Native.SIZE(bmp.Width, bmp.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            if (!Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
                Log.Write($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (!_visibleOnScreen)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            _visibleOnScreen = true;
        }
    }

    private void AttachTo(IntPtr taskbar)
    {
        if (_parent == taskbar) return;

        long style = Native.GetWindowLongPtr(Handle, Native.GWL_STYLE).ToInt64();
        style = (style & ~Native.WS_POPUP) | Native.WS_CHILD;
        Native.SetWindowLongPtr(Handle, Native.GWL_STYLE, new IntPtr(style));
        if (Native.SetParent(Handle, taskbar) == IntPtr.Zero)
            Log.Write($"SetParent failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        _parent = taskbar;
        Log.Write($"attached to taskbar 0x{taskbar.ToInt64():X}");
    }

    private void SetHidden()
    {
        if (!_visibleOnScreen) return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        _visibleOnScreen = false;
    }

    internal static char Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => GlyphMouse,
        DeviceKind.Keyboard => GlyphKeyboard,
        DeviceKind.Gamepad => GlyphGamepad,
        DeviceKind.Headset => GlyphHeadset,
        _ => GlyphOther,
    };

    private static string PickFont(params string[] names)
    {
        using var installed = new InstalledFontCollection();
        foreach (var name in names)
            if (installed.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        return SystemFonts.DefaultFont.Name;
    }
}
