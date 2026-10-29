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
/// мышь проходит сквозь пустые места, а устройства можно перетаскивать; фокус не забирает. Будучи дочерним, оно всегда в одном слое с панелью —
/// не прячется под неё при клике и при открытом «Пуске».
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const char GlyphMouse = '';
    private const char GlyphKeyboard = '';
    private const char GlyphGamepad = '';
    private const char GlyphHeadset = '';
    private const char GlyphOther = '';

    internal static readonly string IconFontName = PickFont("Segoe Fluent Icons", "Segoe MDL2 Assets");
    private static readonly string TextFontName = PickFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");

    private bool _visibleOnScreen;
    private IntPtr _parent;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "BatteryBar";
        _frames.Tick += OnFrame;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frames.Dispose();
            _scene?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Без WS_EX_TRANSPARENT: у слоистого окна клики и так проходят сквозь полностью прозрачные пиксели,
            // а под устройствами мы рисуем почти невидимую подложку, чтобы их можно было схватить.
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Родительская панель задач уничтожена (перезапуск Проводника) — вместе с ней пропало и это окно.</summary>
    public bool IsOrphaned => _parent != IntPtr.Zero && !Native.IsWindow(_parent);

    /// <summary>
    /// Окно оторвалось от панели задач, хотя она жива (бывает при сворачивании полноэкранной игры).
    /// Без родителя координаты «внутри панели» становятся экранными — блок уезжает в верх экрана.
    /// </summary>
    public bool IsDetached => _parent != IntPtr.Zero && IsHandleCreated && Native.GetAncestor(Handle, Native.GA_PARENT) != _parent;

    /// <summary>XAML-содержимое панели может перекрыть нас среди соседних окон — поднимаемся наверх.</summary>
    public void KeepOnTop()
    {
        if (_visibleOnScreen)
            Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    // Анимация: устройство, которое появляется, сначала раздвигает соседей, потом проявляется;
    // исчезающее сначала гаснет, потом его место плавно схлопывается. Позиции считаются заново в каждом кадре.
    private const double AnimSeconds = 0.32;
    private readonly List<Slot> _slots = new();
    private readonly System.Windows.Forms.Timer _frames = new() { Interval = 15 };
    private DateTime _lastFrame;
    private Scene? _scene;

    /// <summary>Место в блоке: устройство или метка «+N». Progress — 0 (нет) … 1 (полностью на месте).</summary>
    private sealed class Slot
    {
        public required string Key;
        public Item? Item;
        public string? More;
        public float Length;
        public double Progress;
        public bool Leaving;

        // Где место стоит сейчас и где должно стоять по раскладке (вдоль панели, от начала окна).
        public float? Pos;
        public float Target;

        // Первая половина анимации — место, вторая — прозрачность (при исчезновении наоборот).
        public float Size => Ease(Math.Clamp(Progress * 2, 0, 1));
        public float Opacity => Ease(Math.Clamp(Progress * 2 - 1, 0, 1));
        private static float Ease(double t) => (float)(t * t * (3 - 2 * t));
    }

    private sealed record Scene(TaskbarLayout Layout, float S, Palette Colors, Fonts Fonts, float Gap, SeparatorStyle Separator) : IDisposable
    {
        public void Dispose()
        {
            Fonts.Text.Dispose();
            Fonts.Badge.Dispose();
        }
    }

    public void Render(IReadOnlyList<DeviceBattery> devices, TaskbarLayout? layout, SeparatorStyle separator)
    {
        if (layout == null || layout.SlotEnd - layout.SlotStart < 20)
        {
            _slots.Clear();
            _frames.Stop();
            SetHidden();
            return;
        }

        float s = layout.Dpi / 96f;
        // На вертикальной панели ширина всего ~48 px — текст чуть мельче, чтобы «100%» поместилось.
        float iconSize = (layout.Vertical ? 19 : 20) * s;
        float boltH = (layout.Vertical ? 11 : 13) * s;
        var textFont = new Font(TextFontName, (layout.Vertical ? 12 : 14) * s, GraphicsUnit.Pixel);
        var badgeFont = new Font(TextFontName, (layout.Vertical ? 9 : 10.5f) * s, FontStyle.Bold, GraphicsUnit.Pixel);
        var fmt = StringFormat.GenericTypographic;

        var measured = new List<Item>();
        float moreW;
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
                    iconSize,
                    d.Badge is { Length: > 0 } badge ? Width(badge, badgeFont) + 2 * s : 0,
                    Width(text, textFont),
                    d.State == ChargeState.Charging ? boltH * DeviceIcons.BoltAspect + 2 * s : 0));
            }
            moreW = Width($"+{devices.Count}", textFont); // самый длинный вариант, чтобы место точно хватило
        }

        _scene?.Dispose();
        float gap = (layout.Vertical ? 14 : 20) * s;
        _scene = new Scene(layout, s, new Palette(layout.LightTheme), new Fonts(iconSize, boltH, textFont, badgeFont, fmt), gap, separator);

        // Длина устройства вдоль панели: у горизонтальной — ширина строки, у вертикальной — две строки.
        var lengths = measured.Select(it => layout.Vertical
            ? (VIconRow + VTextRow) * s
            : it.IconW + (it.BadgeW > 0 ? it.BadgeW + 1 * s : 0) + 5 * s + it.TextW + it.BoltW).ToList();
        float moreLen = layout.Vertical ? VMoreRow * s : moreW;
        float avail = layout.SlotEnd - layout.SlotStart - 2 * Pad * s;

        // Сколько устройств влезает в данную длину.
        int Fit(float room)
        {
            int n = 0;
            float used = 0;
            foreach (float len in lengths)
            {
                float next = used + (n > 0 ? gap : 0) + len;
                if (next > room) break;
                used = next;
                n++;
            }
            return n;
        }

        // Не влезают все — показываем сколько поместится и метку «+N» с числом оставшихся.
        int count = Fit(avail);
        int hidden = 0;
        if (count < measured.Count)
        {
            count = Fit(avail - gap - moreLen);
            hidden = measured.Count - count;
        }

        var wanted = new List<(string Key, Item? Item, string? More, float Length)>();
        for (int i = 0; i < count; i++)
            wanted.Add((measured[i].Dev.Id, measured[i], null, lengths[i]));
        if (hidden > 0)
            wanted.Add(("+", null, $"+{hidden}", moreLen));
        Merge(wanted);
        Draw();
        EnsureFrames();
    }

    private bool Moving => _drag != null || _grab > 0 || _slots.Any(sl =>
        (sl.Leaving ? sl.Progress > 0 : sl.Progress < 1) || (sl.Pos is { } p && Math.Abs(p - sl.Target) > 0.5f));

    private void EnsureFrames()
    {
        if (!Moving || _frames.Enabled) return;
        _lastFrame = DateTime.UtcNow;
        _frames.Start();
    }

    /// <summary>
    /// Новый набор мест. Оставшиеся устройства сохраняют анимацию, новые начинают с нуля,
    /// пропавшие остаются на старом месте (после того же соседа) и исчезают.
    /// </summary>
    private void Merge(List<(string Key, Item? Item, string? More, float Length)> wanted)
    {
        if (_drag != null)
        {
            var onScreen = _slots.Select(sl => sl.Key).ToList();
            wanted = wanted.OrderBy(w => w.Key == "+" ? int.MaxValue : onScreen.IndexOf(w.Key) is var i && i >= 0 ? i : int.MaxValue - 1).ToList();
        }
        var old = _slots.ToList();
        var byKey = old.ToDictionary(sl => sl.Key);
        _slots.Clear();
        foreach (var w in wanted)
        {
            if (!byKey.TryGetValue(w.Key, out var slot))
                slot = new Slot { Key = w.Key, Progress = 0 };
            slot.Item = w.Item;
            slot.More = w.More;
            slot.Length = w.Length;
            slot.Leaving = false;
            _slots.Add(slot);
        }

        var keep = wanted.Select(w => w.Key).ToHashSet();
        for (int i = 0; i < old.Count; i++)
        {
            var slot = old[i];
            if (keep.Contains(slot.Key) || slot.Progress <= 0) continue;
            slot.Leaving = true;
            // Ставим после ближайшего предыдущего соседа, который остался.
            int at = 0;
            for (int j = i - 1; j >= 0; j--)
            {
                int idx = _slots.IndexOf(old[j]);
                if (idx >= 0) { at = idx + 1; break; }
            }
            _slots.Insert(at, slot);
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        foreach (var slot in _slots)
            slot.Progress = Math.Clamp(slot.Progress + (slot.Leaving ? -dt : dt) / AnimSeconds, 0, 1);
        // Подложка под схваченным значком проявляется за 0,15 с и так же гаснет после отпускания.
        _grab = Math.Clamp(_grab + (float)(_drag != null ? dt : -dt) / 0.15f, 0, 1);
        if (_grab <= 0 && _drag == null) _grabSlot = null;
        _slots.RemoveAll(sl => sl.Leaving && sl.Progress <= 0);

        // Места догоняют расчётные позиции: соседи плавно расступаются вокруг перетаскиваемого значка.
        Arrange();
        float k = (float)(1 - Math.Exp(-SlideSpeed * dt));
        foreach (var slot in _slots)
            if (slot != _drag?.Slot && slot.Pos is { } p)
                slot.Pos = Math.Abs(slot.Target - p) < 0.5f ? slot.Target : p + (slot.Target - p) * k;
        PaintFrame();
        if (!Moving) _frames.Stop();
    }

    // Скорость, с которой место догоняет свою позицию: за 1/60 с — около 23% пути.
    private const double SlideSpeed = 16;

    private const float Pad = 2;
    private const float VIconRow = 22, VTextRow = 17, VMoreRow = 15;

    private sealed record Item(DeviceBattery Dev, string Text, float IconW, float BadgeW, float TextW, float BoltW)
    {
        public bool IsLow => Dev.Percent is <= 20 && Dev.State != ChargeState.Charging;
    }

    private sealed record Fonts(float IconSize, float BoltH, Font Text, Font Badge, StringFormat Format);

    private sealed record Palette(bool Light)
    {
        public Color Fg => Light ? Color.FromArgb(235, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        public Color Low => Light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 153, 164);
        public Color Charge => Light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95);
    }

    private static Color Fade(Color c, float opacity) => Color.FromArgb((int)(c.A * opacity), c);

    /// <summary>
    /// Кадр: окно занимает всё свободное место на панели (оно прозрачное и пропускает мышь там, где пусто),
    /// а устройства стоят в начале, посередине или в конце этого места.
    /// </summary>
    private void Draw()
    {
        Arrange();
        foreach (var slot in _slots) slot.Pos ??= slot.Target;
        PaintFrame();
    }

    /// <summary>Расчётные позиции мест по текущему порядку и стадиям анимации.</summary>
    private void Arrange()
    {
        if (_scene is not { } sc) return;
        var layout = sc.Layout;
        float pad = Pad * sc.S;
        int slotLen = layout.SlotEnd - layout.SlotStart;

        float total = 0;
        for (int i = 0; i < _slots.Count; i++)
            total += (i > 0 ? sc.Gap * _slots[i].Size : 0) + _slots[i].Length * _slots[i].Size;
        // По центру — от середины полосы: при появлении устройства блок растёт в обе стороны.
        float pos = layout.Centered ? (slotLen - total) / 2 : layout.EndAligned ? slotLen - pad - total : pad;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i > 0) pos += sc.Gap * _slots[i].Size;
            _slots[i].Target = pos;
            pos += _slots[i].Length * _slots[i].Size;
        }
    }

    private void PaintFrame()
    {
        if (_scene is not { } sc || _slots.Count == 0)
        {
            SetHidden();
            return;
        }
        var layout = sc.Layout;
        int slotLen = layout.SlotEnd - layout.SlotStart;
        int across = layout.Vertical ? layout.Taskbar.Width : layout.Taskbar.Height;
        int w = layout.Vertical ? across : slotLen, h = layout.Vertical ? slotLen : across;

        // Всё, кроме схваченного значка, — отдельным слоем: под подложкой его покажем размытым, как сквозь матовое стекло.
        using var under = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = NewCanvas(under))
        {
            // Линии — посередине расчётных отступов: пока значок тянут, они отмечают, куда он встанет.
            if (sc.Separator == SeparatorStyle.Line)
                for (int i = 1; i < _slots.Count; i++)
                {
                    var prev = _slots[i - 1];
                    DrawSeparator(g, (prev.Target + prev.Length * prev.Size + _slots[i].Target) / 2, across, _slots[i], sc);
                }
            foreach (var slot in _slots)
                if (slot != _grabSlot || _grab <= 0)
                    DrawSlot(g, slot, across, sc);
        }

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = NewCanvas(bmp))
        {
            if (_grabSlot is { } grabbed && _grab > 0 && grabbed.Opacity > 0)
            {
                float at = grabbed.Pos ?? grabbed.Target;
                var rect = GrabRect(at, grabbed.Length, across, sc);
                using var path = RoundedRect(rect, 6 * sc.S);
                // Снаружи подложки — как есть; внутри — размытое (по мере проявления подложки).
                g.SetClip(path, CombineMode.Exclude);
                g.DrawImageUnscaled(under, 0, 0);
                g.ResetClip();
                g.SetClip(path);
                DrawFaded(g, under, 1 - _grab);
                var (blurred, origin) = Blurred(under, Rectangle.Round(rect), (int)MathF.Round(4 * sc.S));
                using (blurred)
                    DrawFaded(g, blurred, _grab, origin);
                g.ResetClip();
                DrawGrabBackground(g, path, sc);
                DrawSlot(g, grabbed, across, sc);
            }
            else
            {
                g.DrawImageUnscaled(under, 0, 0);
            }
        }

        if (layout.Vertical)
            Present(bmp, 0, layout.SlotStart - layout.Taskbar.Top, layout.Handle);
        else
            Present(bmp, layout.SlotStart - layout.Taskbar.Left, 0, layout.Handle);
    }

    private void DrawSlot(Graphics g, Slot slot, int across, Scene sc)
    {
        if (slot.Opacity <= 0) return;
        float at = slot.Pos ?? slot.Target;
        if (slot.Item != null)
        {
            // Почти невидимая подложка: без неё клик между пикселями значка и текста ушёл бы панели задач.
            using var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
            if (sc.Layout.Vertical) g.FillRectangle(hit, 0, at, across, slot.Length);
            else g.FillRectangle(hit, at, 0, slot.Length, across);
        }
        if (sc.Layout.Vertical) DrawVertical(g, slot, at, across, sc);
        else DrawHorizontal(g, slot, at, across, sc);
    }

    private static void DrawFaded(Graphics g, Image image, float opacity, Point origin = default)
    {
        if (opacity <= 0) return;
        if (opacity >= 1)
        {
            g.DrawImageUnscaled(image, origin.X, origin.Y);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
        g.DrawImage(image, new Rectangle(origin, image.Size), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>
    /// Кусок слоя, размытый тремя проходами прямоугольного фильтра (выходит почти как гауссово размытие).
    /// Слой с предумноженной альфой — поэтому края значков не темнеют.
    /// </summary>
    private static (Bitmap Image, Point Origin) Blurred(Bitmap layer, Rectangle area, int radius)
    {
        area.Inflate(radius * 2, radius * 2);
        area.Intersect(new Rectangle(Point.Empty, layer.Size));
        var piece = layer.Clone(area, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        if (radius < 1 || area.Width < 2 || area.Height < 2) return (piece, area.Location);

        var data = piece.LockBits(new Rectangle(Point.Empty, piece.Size), System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        int w = piece.Width, h = piece.Height, stride = data.Stride;
        var px = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxPass(px, tmp, w, h, stride, radius, horizontal: true);
            BoxPass(tmp, px, w, h, stride, radius, horizontal: false);
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        piece.UnlockBits(data);
        return (piece, area.Location);
    }

    private static void BoxPass(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int Index(int line, int i) => horizontal ? line * stride + i * 4 : i * stride + line * 4;
        int window = 2 * r + 1;
        Span<int> sum = stackalloc int[4];
        for (int line = 0; line < lines; line++)
        {
            sum.Clear();
            // Края продолжаем крайним пикселем.
            for (int i = -r; i <= r; i++)
            {
                int k = Index(line, Math.Clamp(i, 0, len - 1));
                for (int c = 0; c < 4; c++) sum[c] += src[k + c];
            }
            for (int i = 0; i < len; i++)
            {
                int k = Index(line, i);
                for (int c = 0; c < 4; c++) dst[k + c] = (byte)(sum[c] / window);
                int add = Index(line, Math.Min(i + r + 1, len - 1)), drop = Index(line, Math.Max(i - r, 0));
                for (int c = 0; c < 4; c++) sum[c] += src[add + c] - src[drop + c];
            }
        }
    }

    // Схваченный значок и стадия его подложки (0 — нет, 1 — видна полностью).
    private Slot? _grabSlot;
    private float _grab;

    private static RectangleF GrabRect(float at, float length, int across, Scene sc)
    {
        float s = sc.S, padAlong = 7 * s, padAcross = 5 * s;
        return sc.Layout.Vertical
            ? new RectangleF(padAcross, at - padAlong, across - 2 * padAcross, length + 2 * padAlong)
            : new RectangleF(at - padAlong, padAcross, length + 2 * padAlong, across - 2 * padAcross);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, 2 * r, 2 * r, 180, 90);
        path.AddArc(rect.Right - 2 * r, rect.Top, 2 * r, 2 * r, 270, 90);
        path.AddArc(rect.Right - 2 * r, rect.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Матовая подложка: заливка поверх размытого и тонкая светлая обводка, как у стеклянных элементов Windows 11.</summary>
    private void DrawGrabBackground(Graphics g, GraphicsPath path, Scene sc)
    {
        bool light = sc.Layout.LightTheme;
        using var fill = new SolidBrush(Fade(light ? Color.FromArgb(150, 249, 249, 249) : Color.FromArgb(150, 60, 60, 60), _grab));
        g.FillPath(fill, path);
        using var stroke = new Pen(Fade(light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(45, 255, 255, 255), _grab), Math.Max(1, MathF.Round(sc.S)));
        g.DrawPath(stroke, path);
    }

    // Перетаскивание значков мышью. Порог — чтобы обычный клик не сдвигал значок.
    private sealed record DragState(Slot Slot, float GrabOffset, float StartAt)
    {
        public bool Started { get; set; }
    }

    private DragState? _drag;

    /// <summary>Пользователь перетащил значок: новый порядок Id видимых устройств.</summary>
    public event Action<IReadOnlyList<string>>? Reordered;

    private float Along(MouseEventArgs e) => _scene?.Layout.Vertical == true ? e.Y : e.X;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        float at = Along(e);
        var slot = _slots.FirstOrDefault(sl => sl.Item != null && !sl.Leaving && sl.Pos is { } p && at >= p && at <= p + sl.Length);
        if (slot == null) return;
        _drag = new DragState(slot, at - slot.Pos!.Value, at);
        _grabSlot = slot;
        Capture = true;
        EnsureFrames();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is not { } d || _scene is not { } sc) return;
        float at = Along(e);
        if (!d.Started)
        {
            if (Math.Abs(at - d.StartAt) < 5 * sc.S) return;
            d.Started = true;
            Cursor = Cursors.SizeAll;
        }

        int slotLen = sc.Layout.SlotEnd - sc.Layout.SlotStart;
        d.Slot.Pos = Math.Clamp(at - d.GrabOffset, 0, slotLen - d.Slot.Length);

        // Край значка зашёл за середину соседа — меняемся местами. «+N» всегда остаётся последним.
        float lo = d.Slot.Pos.Value, hi = lo + d.Slot.Length;
        int i = _slots.IndexOf(d.Slot);
        while (i + 1 < _slots.Count && _slots[i + 1].Item != null && hi > _slots[i + 1].Target + _slots[i + 1].Length / 2)
        {
            (_slots[i], _slots[i + 1]) = (_slots[i + 1], _slots[i]);
            i++;
            Arrange();
        }
        while (i > 0 && _slots[i - 1].Item != null && lo < _slots[i - 1].Target + _slots[i - 1].Length / 2)
        {
            (_slots[i], _slots[i - 1]) = (_slots[i - 1], _slots[i]);
            i--;
            Arrange();
        }
        EnsureFrames();
        Arrange();
        PaintFrame();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag is not { } d) return;
        _drag = null;
        Capture = false;
        Cursor = Cursors.Default;
        EnsureFrames();
        if (!d.Started) return;
        // Значок доезжает до своего нового места, порядок сохраняет приложение.
        EnsureFrames();
        Reordered?.Invoke(_slots.Where(sl => sl.Item != null && !sl.Leaving).Select(sl => sl.Key).ToList());
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // Захват мыши отобрали (Alt+Tab, всплывающее окно) — отпускаем значок там, где он есть.
        if (_drag != null && !Capture) OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
    }

    /// <summary>
    /// Тонкая линия посередине отступа, поперёк панели. Появляется и исчезает вместе с соседом:
    /// пока его место раздвигается, линия проявляется.
    /// </summary>
    private static void DrawSeparator(Graphics g, float at, int across, Slot next, Scene sc)
    {
        float s = sc.S;
        float length = (sc.Layout.Vertical ? 22 : 20) * s;
        float thick = Math.Max(1, MathF.Round(s));
        using var brush = new SolidBrush(Fade(Color.FromArgb(sc.Layout.LightTheme ? 70 : 85, sc.Colors.Fg), next.Size));
        float lo = (across - length) / 2f;
        // Целые пиксели — иначе сглаживание размывает линию в две бледные.
        if (sc.Layout.Vertical)
            g.FillRectangle(brush, MathF.Round(lo), MathF.Round(at - thick / 2), MathF.Round(length), thick);
        else
            g.FillRectangle(brush, MathF.Round(at - thick / 2), MathF.Round(lo), thick, MathF.Round(length));
    }

    /// <summary>Горизонтальная панель: значок, подпись, процент, молния — в строку.</summary>
    private static void DrawHorizontal(Graphics g, Slot slot, float x, int height, Scene sc)
    {
        float s = sc.S, a = slot.Opacity;
        var c = sc.Colors;
        var f = sc.Fonts;
        if (slot.Item is not { } it)
        {
            using var moreBrush = new SolidBrush(Fade(Color.FromArgb(190, c.Fg), a));
            DrawCentered(g, slot.More!, f.Text, moreBrush, x, height, f.Format);
            return;
        }

        using var brush = new SolidBrush(Fade(it.IsLow ? c.Low : c.Fg, a));
        DeviceIcons.Draw(g, it.Dev, x, (height - f.IconSize) / 2f, f.IconSize, brush);
        float cx = x + it.IconW;
        if (it.BadgeW > 0)
        {
            using var badgeBrush = new SolidBrush(Fade(Color.FromArgb(190, it.IsLow ? c.Low : c.Fg), a));
            DrawCentered(g, it.Dev.Badge!, f.Badge, badgeBrush, cx + 3 * s, height, f.Format);
            cx += it.BadgeW + 1 * s;
        }
        cx += 5 * s;
        DrawCentered(g, it.Text, f.Text, brush, cx, height, f.Format);
        if (it.BoltW > 0)
        {
            using var boltBrush = new SolidBrush(Fade(c.Charge, a));
            DeviceIcons.DrawBolt(g, cx + it.TextW + 2 * s, (height - f.BoltH) / 2f, f.BoltH, boltBrush);
        }
    }

    /// <summary>Вертикальная панель (сбоку): у каждого устройства две строки — значок с подписью и процент.</summary>
    private static void DrawVertical(Graphics g, Slot slot, float y, int width, Scene sc)
    {
        float s = sc.S, a = slot.Opacity;
        float iconRow = VIconRow * s, textRow = VTextRow * s;
        var c = sc.Colors;
        var f = sc.Fonts;
        if (slot.Item is not { } it)
        {
            using var moreBrush = new SolidBrush(Fade(Color.FromArgb(190, c.Fg), a));
            float mw = g.MeasureString(slot.More!, f.Text, PointF.Empty, f.Format).Width;
            DrawInRow(g, slot.More!, f.Text, moreBrush, (width - mw) / 2f, y, VMoreRow * s, f.Format);
            return;
        }

        using var brush = new SolidBrush(Fade(it.IsLow ? c.Low : c.Fg, a));

        // Строка 1: значок (+ подпись PS/XB), по центру панели.
        float row1 = it.IconW + it.BadgeW;
        float x1 = (width - row1) / 2f;
        DeviceIcons.Draw(g, it.Dev, x1, y + (iconRow - f.IconSize) / 2f, f.IconSize, brush);
        if (it.BadgeW > 0)
        {
            using var badgeBrush = new SolidBrush(Fade(Color.FromArgb(190, it.IsLow ? c.Low : c.Fg), a));
            DrawInRow(g, it.Dev.Badge!, f.Badge, badgeBrush, x1 + it.IconW + 2 * s, y, iconRow, f.Format);
        }

        // Строка 2: процент (+ молния), по центру панели.
        float row2 = it.TextW + it.BoltW;
        float x2 = (width - row2) / 2f;
        DrawInRow(g, it.Text, f.Text, brush, x2, y + iconRow, textRow, f.Format);
        if (it.BoltW > 0)
        {
            using var boltBrush = new SolidBrush(Fade(c.Charge, a));
            DeviceIcons.DrawBolt(g, x2 + it.TextW + 2 * s, y + iconRow + (textRow - f.BoltH) / 2f, f.BoltH, boltBrush);
        }
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
