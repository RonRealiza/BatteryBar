using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BatteryBar;

/// <summary>
/// Заполненные значки устройств (сплошной силуэт с вырезанными деталями) — для блока на панели задач и иконок трея.
/// В Segoe Fluent Icons у этих значков есть только контурные версии, которые в 16 px почти не видны, поэтому
/// рисуем свои. Координаты — на сетке 24×24; вырезы — внутренние контуры при заливке «чёт-нечет» (FillMode.Alternate).
/// </summary>
internal static class DeviceIcons
{
    private const float Grid = 24f;

    /// <summary>Нарисовать значок устройства в квадрате size×size с левым верхним углом (x, y).</summary>
    public static void Draw(Graphics g, DeviceBattery device, float x, float y, float size, Brush brush) =>
        Draw(g, IconOf(device), x, y, size, brush);

    /// <summary>
    /// Значок устройства: выбранный пользователем или подсказанный модулем (вкладыши, колонка), иначе — по типу.
    /// У геймпадов свой значок для PlayStation и Xbox: по подписи у значка (PS, PS4, XB…), а у Bluetooth-геймпадов
    /// без подписи — по названию.
    /// </summary>
    public static DeviceIcon IconOf(DeviceBattery d)
    {
        if (d.Icon is { } chosen) return chosen;
        switch (d.Kind)
        {
            case DeviceKind.Mouse: return DeviceIcon.Mouse;
            case DeviceKind.Keyboard: return DeviceIcon.Keyboard;
            case DeviceKind.Headset: return DeviceIcon.Headset;
            case DeviceKind.Gamepad:
                string badge = d.Badge ?? "", name = d.Name;
                if (badge.StartsWith("PS") || name.Contains("DualSense", System.StringComparison.OrdinalIgnoreCase)
                    || name.Contains("DualShock", System.StringComparison.OrdinalIgnoreCase))
                    return DeviceIcon.PlayStation;
                if (badge.StartsWith("XB") || name.Contains("Xbox", System.StringComparison.OrdinalIgnoreCase))
                    return DeviceIcon.Xbox;
                return DeviceIcon.Gamepad;
            default: return DeviceIcon.Other;
        }
    }

    /// <summary>
    /// Все значки в нескольких размерах на тёмном и светлом фоне — для проверки дизайна (ключ запуска --icons-preview).
    /// </summary>
    public static void SavePreview(string file)
    {
        var shapes = (DeviceIcon[])System.Enum.GetValues(typeof(DeviceIcon));
        int[] sizes = { 96, 48, 24, 20, 16 };
        int cell = 110, w = cell * sizes.Length, h = cell * (shapes.Length + 1); // последняя строка — молния зарядки
        using var bmp = new Bitmap(w, h * 2);
        using var g = Graphics.FromImage(bmp);
        for (int half = 0; half < 2; half++)
        {
            bool light = half == 1;
            g.FillRectangle(new SolidBrush(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32)), 0, half * h, w, h);
            using var brush = new SolidBrush(light ? Color.Black : Color.White);
            for (int r = 0; r < shapes.Length; r++)
                for (int c = 0; c < sizes.Length; c++)
                {
                    float s = sizes[c];
                    Draw(g, shapes[r], c * cell + (cell - s) / 2, half * h + r * cell + (cell - s) / 2, s, brush);
                }
            using var charge = new SolidBrush(light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95));
            for (int c = 0; c < sizes.Length; c++)
            {
                float s = sizes[c];
                DrawBolt(g, c * cell + (cell - s * BoltAspect) / 2, half * h + shapes.Length * cell + (cell - s) / 2, s, charge);
            }
        }
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Draw(Graphics g, DeviceIcon shape, float x, float y, float size, Brush brush)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(x, y);
        g.ScaleTransform(size / Grid, size / Grid);

        using var path = PathOf(shape);
        g.FillPath(brush, path);
        g.Restore(state);
    }

    private static GraphicsPath PathOf(DeviceIcon shape) => shape switch
    {
        DeviceIcon.Mouse => Mouse(),
        DeviceIcon.Keyboard => Keyboard(),
        DeviceIcon.Headset => Headset(),
        DeviceIcon.Earbuds => Earbuds(),
        DeviceIcon.Speaker => Speaker(),
        DeviceIcon.Gamepad => Svg("gamepad-other"),
        DeviceIcon.PlayStation => Svg("gamepad-ps"),
        DeviceIcon.Xbox => Svg("gamepad-xbox"),
        DeviceIcon.Phone => Phone(),
        DeviceIcon.Watch => Watch(),
        _ => Other(),
    };

    /// <summary>Тот же значок для WPF (окно настроек) — векторный, на сетке 24×24.</summary>
    public static System.Windows.Media.Geometry Geometry(DeviceBattery device) => Geometry(IconOf(device));

    public static System.Windows.Media.Geometry Geometry(DeviceIcon icon)
    {
        using var path = PathOf(icon);
        return ToWpf(path);
    }

    /// <summary>Молния зарядки для WPF, на сетке 24×24 (ширина BoltAspect·24).</summary>
    public static System.Windows.Media.Geometry BoltGeometry()
    {
        using var path = Bolt();
        return ToWpf(path);
    }

    private static System.Windows.Media.Geometry ToWpf(GraphicsPath path)
    {
        var points = path.PathPoints;
        var types = path.PathTypes;
        var geometry = new System.Windows.Media.StreamGeometry
        {
            FillRule = path.FillMode == FillMode.Winding
                ? System.Windows.Media.FillRule.Nonzero
                : System.Windows.Media.FillRule.EvenOdd,
        };
        using (var ctx = geometry.Open())
        {
            static System.Windows.Point P(PointF p) => new(p.X, p.Y);
            for (int i = 0; i < points.Length; i++)
            {
                // Замыкать фигуры явно не нужно: при заливке WPF и так соединяет конец контура с началом.
                switch ((PathPointType)types[i] & PathPointType.PathTypeMask)
                {
                    case PathPointType.Start:
                        ctx.BeginFigure(P(points[i]), true, false);
                        break;
                    case PathPointType.Bezier3:
                        ctx.BezierTo(P(points[i]), P(points[i + 1]), P(points[i + 2]), true, true);
                        i += 2;
                        break;
                    default:
                        ctx.LineTo(P(points[i]), true, true);
                        break;
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Заполненная молния «заряжается» в прямоугольнике шириной BoltAspect·height и высотой height.</summary>
    public static void DrawBolt(Graphics g, float x, float y, float height, Brush brush)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(x, y);
        g.ScaleTransform(height / Grid, height / Grid);
        using var path = Bolt();
        g.FillPath(brush, path);
        g.Restore(state);
    }

    private static GraphicsPath Bolt()
    {
        var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(10.2f, 0.5f), new PointF(1f, 13.6f), new PointF(7.6f, 13.6f), new PointF(5.8f, 23.5f),
            new PointF(15.4f, 9.6f), new PointF(8.8f, 9.6f), new PointF(10.2f, 0.5f),
        });
        return path;
    }

    /// <summary>Отношение ширины молнии к её высоте.</summary>
    public const float BoltAspect = 16.4f / Grid;

    /// <summary>Мышь: сплошной корпус, верх с кнопками — «окно» с колёсиком внутри.</summary>
    private static GraphicsPath Mouse()
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddPath(Capsule(5.5f, 1.5f, 13f, 21f), false);

        // Вырез области кнопок над разделительной линией.
        var window = new GraphicsPath();
        window.AddArc(7.4f, 3.4f, 9.2f, 9.2f, 180, 180);
        window.AddLine(16.6f, 8f, 16.6f, 9.6f);
        window.AddLine(16.6f, 9.6f, 7.4f, 9.6f);
        window.CloseFigure();
        p.AddPath(window, false);

        // Колёсико — снова закрашено (третий уровень вложенности).
        p.AddPath(RoundedRect(10.9f, 4.6f, 2.2f, 4f, 1.1f), false);
        return p;
    }

    /// <summary>Клавиатура: сплошной корпус с вырезанными клавишами и пробелом.</summary>
    private static GraphicsPath Keyboard()
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddPath(RoundedRect(0.8f, 5f, 22.4f, 14f, 3f), false);
        float[] xs = { 3.6f, 7.3f, 11f, 14.7f, 18.4f };
        foreach (float kx in xs)
        {
            p.AddRectangle(new RectangleF(kx, 7.8f, 2f, 2f));
            p.AddRectangle(new RectangleF(kx, 11.2f, 2f, 2f));
        }
        p.AddPath(RoundedRect(7.3f, 14.8f, 9.4f, 1.9f, 0.9f), false);
        return p;
    }

    private static readonly Dictionary<string, GraphicsPath> SvgCache = new();

    /// <summary>
    /// Силуэт из assets\icons\{name}.svg (встроен в exe), вписанный по ширине в сетку 24×24 и выровненный по центру.
    /// </summary>
    private static GraphicsPath Svg(string name)
    {
        lock (SvgCache)
        {
            if (!SvgCache.TryGetValue(name, out var cached))
            {
                using var stream = typeof(DeviceIcons).Assembly.GetManifestResourceStream($"icons.{name}.svg")!;
                string text = new StreamReader(stream).ReadToEnd();
                var box = Regex.Match(text, @"viewBox=""([^""]+)""").Groups[1].Value
                    .Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                float w = Num(box[2]), h = Num(box[3]);
                cached = new GraphicsPath(FillMode.Alternate);
                foreach (Match m in Regex.Matches(text, @"\sd=""([^""]+)"""))
                    AddSvgPath(cached, m.Groups[1].Value);
                float k = Grid / System.Math.Max(w, h);
                using var fit = new Matrix();
                fit.Translate((Grid - w * k) / 2, (Grid - h * k) / 2);
                fit.Scale(k, k);
                cached.Transform(fit);
                SvgCache[name] = cached;
            }
            return (GraphicsPath)cached.Clone();
        }
    }

    private static float Num(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Разбор атрибута d: команды M/L/H/V/C/S/Z в обоих регистрах — всё, что выдают редакторы для силуэтов.</summary>
    private static void AddSvgPath(GraphicsPath path, string d)
    {
        var tokens = Regex.Matches(d, @"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
        int i = 0;
        char cmd = ' ';
        PointF cur = default, start = default, ctrl = default;
        bool figure = false;
        float Next() => Num(tokens[i++].Value);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i].Value[0])) cmd = tokens[i++].Value[0];
            bool rel = char.IsLower(cmd);
            PointF Pt(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    if (figure) path.CloseFigure();
                    path.StartFigure();
                    cur = start = ctrl = Pt(Next(), Next());
                    figure = true;
                    cmd = rel ? 'l' : 'L'; // следующие пары после M — это линии
                    break;
                case 'L':
                    { var p = Pt(Next(), Next()); path.AddLine(cur, p); cur = ctrl = p; }
                    break;
                case 'H':
                    { var p = new PointF(rel ? cur.X + Next() : Next(), cur.Y); path.AddLine(cur, p); cur = ctrl = p; }
                    break;
                case 'V':
                    { var p = new PointF(cur.X, rel ? cur.Y + Next() : Next()); path.AddLine(cur, p); cur = ctrl = p; }
                    break;
                case 'C':
                    {
                        var c1 = Pt(Next(), Next()); var c2 = Pt(Next(), Next()); var p = Pt(Next(), Next());
                        path.AddBezier(cur, c1, c2, p); ctrl = c2; cur = p;
                    }
                    break;
                case 'S':
                    {
                        var c1 = new PointF(2 * cur.X - ctrl.X, 2 * cur.Y - ctrl.Y);
                        var c2 = Pt(Next(), Next()); var p = Pt(Next(), Next());
                        path.AddBezier(cur, c1, c2, p); ctrl = c2; cur = p;
                    }
                    break;
                case 'Z':
                    path.CloseFigure();
                    cur = ctrl = start;
                    figure = false;
                    continue;
                default:
                    throw new InvalidDataException($"SVG: команда {cmd} не поддерживается");
            }
            // Числа без новой буквы — повтор той же команды (cmd не меняется).
        }
        if (figure) path.CloseFigure();
    }

    /// <summary>Гарнитура: дуга оголовья (обводка, превращённая в контур) и сплошные амбушюры.</summary>
    private static GraphicsPath Headset()
    {
        // Части перекрываются — заливка «по ненулевому», чтобы на стыках не было дырок.
        var p = new GraphicsPath(FillMode.Winding);
        using var pen = new Pen(Color.Black, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var band = new GraphicsPath();
        band.AddArc(3.6f, 3f, 16.8f, 16.8f, 180, 180);
        band.Widen(pen);
        p.AddPath(band, false);
        foreach (var (x1, x2) in new[] { (3.6f, 3.6f), (20.4f, 20.4f) })
        {
            var stem = new GraphicsPath();
            stem.AddLine(x1, 11.4f, x2, 13f);
            stem.Widen(pen);
            p.AddPath(stem, false);
        }
        p.AddPath(RoundedRect(1.6f, 12.2f, 6.4f, 9.6f, 2.4f), false);
        p.AddPath(RoundedRect(16f, 12.2f, 6.4f, 9.6f, 2.4f), false);
        return p;
    }

    /// <summary>Вкладыши: два наушника-«капли» с ножками, как AirPods.</summary>
    private static GraphicsPath Earbuds()
    {
        // Головка и ножка перекрываются — заливка «по ненулевому».
        var p = new GraphicsPath(FillMode.Winding);
        foreach (float x in new[] { 2.2f, 13.4f })
        {
            p.AddEllipse(x, 2.5f, 8.4f, 8.4f);
            p.AddPath(Capsule(x + 4.6f, 6f, 3.2f, 15.5f), false);
        }
        return p;
    }

    /// <summary>Колонка: корпус с вырезанными динамиками — большим (с колпачком) и маленьким.</summary>
    private static GraphicsPath Speaker()
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddPath(RoundedRect(4.5f, 1.5f, 15f, 21f, 3.2f), false);
        p.AddEllipse(7.2f, 10.2f, 9.6f, 9.6f);
        p.AddEllipse(10.4f, 13.4f, 3.2f, 3.2f);
        p.AddEllipse(10.2f, 4.2f, 3.6f, 3.6f);
        return p;
    }

    /// <summary>Телефон: корпус с вырезанным экраном и «челкой» динамика.</summary>
    private static GraphicsPath Phone()
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddPath(RoundedRect(5.5f, 1.2f, 13f, 21.6f, 3f), false);
        p.AddPath(RoundedRect(7.3f, 5f, 9.4f, 14f, 1.2f), false);
        p.AddPath(RoundedRect(10f, 2.7f, 4f, 1.3f, 0.65f), false);
        return p;
    }

    /// <summary>Часы: ремешок, корпус с вырезанным циферблатом и стрелками, коронка сбоку.</summary>
    private static GraphicsPath Watch()
    {
        var p = new GraphicsPath(FillMode.Winding);
        p.AddPath(RoundedRect(8f, 0.8f, 8f, 6f, 1.6f), false);
        p.AddPath(RoundedRect(8f, 17.2f, 8f, 6f, 1.6f), false);
        p.AddPath(RoundedRect(4.5f, 4.5f, 15f, 15f, 4f), false);
        p.AddPath(RoundedRect(19f, 10f, 1.8f, 4f, 0.7f), false);
        // Циферблат — вырез: в «ненулевой» заливке дырку даёт контур в обратную сторону.
        var face = RoundedRect(6.6f, 6.6f, 10.8f, 10.8f, 2.6f);
        face.Reverse();
        p.AddPath(face, false);
        p.AddPath(RoundedRect(11.2f, 8.4f, 1.6f, 4.4f, 0.8f), false);
        p.AddPath(RoundedRect(11.2f, 11.2f, 3.8f, 1.6f, 0.8f), false);
        return p;
    }

    /// <summary>Прочее устройство: сплошной круг с вырезанной точкой.</summary>
    private static GraphicsPath Other()
    {
        var p = new GraphicsPath(FillMode.Alternate);
        p.AddEllipse(3f, 3f, 18f, 18f);
        p.AddEllipse(9.5f, 9.5f, 5f, 5f);
        return p;
    }

    private static GraphicsPath Capsule(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        float r = w / 2;
        p.AddArc(x, y, w, w, 180, 180);
        p.AddLine(x + w, y + r, x + w, y + h - r);
        p.AddArc(x, y + h - w, w, w, 0, 180);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
