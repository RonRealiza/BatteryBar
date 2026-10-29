using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryBar;

/// <summary>
/// Где на панели задач рисовать блок. Vertical — панель стоит сбоку (слева или справа).
/// Координаты вдоль панели — экранные физические пиксели: X у горизонтальной, Y у вертикальной.
/// LeftZone — свободное место до «Пуска» (за погодой, если она в начале), RightZone — от последнего приложения до трея
/// (или до погоды, если она в конце). Slot — где рисовать сейчас (см. Place); EndAligned — прижимать к концу полосы.
/// Monitor — имя дисплея Windows (\\.\DISPLAY1), по нему запоминается выбор мониторов.
/// </summary>
internal sealed record TaskbarLayout(
    IntPtr Handle,
    string Monitor,
    bool IsPrimary,
    Rectangle Taskbar,
    bool Vertical,
    int Dpi,
    (int Start, int End) LeftZone,
    (int Start, int End) RightZone,
    bool LightTheme)
{
    public int SlotStart { get; init; }
    public int SlotEnd { get; init; }
    public bool EndAligned { get; init; }
    public bool Centered { get; init; }

    /// <summary>
    /// Раскладка с выбранным местом. Если в нужной зоне не хватает места (панель выровнена по левому краю
    /// и «Пуск» стоит у самого края, или приложений так много, что до трея ничего не осталось) — берём другую.
    /// </summary>
    public TaskbarLayout Place(TaskbarPosition position)
    {
        int min = 60 * Dpi / 96;
        bool left = position is TaskbarPosition.AfterWidgets or TaskbarPosition.CenterLeft or TaskbarPosition.BeforeStart;
        bool centered = position is TaskbarPosition.CenterLeft or TaskbarPosition.CenterRight;
        bool atEnd = position is TaskbarPosition.BeforeStart or TaskbarPosition.BeforeTray;
        var zone = left ? LeftZone : RightZone;
        if (zone.End - zone.Start < min)
        {
            // В другой зоне встаём посередине, если так и просили, иначе у её дальнего края: у трея или сразу за погодой.
            left = !left;
            atEnd = !left && !centered;
            zone = left ? LeftZone : RightZone;
        }
        return this with { SlotStart = zone.Start, SlotEnd = zone.End, EndAligned = atEnd, Centered = centered };
    }
}

internal static class TaskbarLocator
{
    // UI Automation изредка не находит кнопки (панель перерисовывается) — тогда берём последнее удачное значение.
    private static readonly Dictionary<IntPtr, (Rectangle Taskbar, (int, int) Left, (int, int) Right)> LastGood = new();

    /// <summary>Главная панель задач и панели на остальных мониторах.</summary>
    public static List<TaskbarLayout> LocateAll()
    {
        bool light = IsLightTheme();
        var result = new List<TaskbarLayout>();

        var primary = Native.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && Locate(primary, true, light) is { } main)
            result.Add(main);

        var secondary = IntPtr.Zero;
        while ((secondary = Native.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (Locate(secondary, false, light) is { } extra)
                result.Add(extra);
        }
        return result;
    }

    private static TaskbarLayout? Locate(IntPtr taskbar, bool isPrimary, bool light)
    {
        if (!Native.GetWindowRect(taskbar, out var r))
            return null;

        var tb = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        bool vertical = tb.Height > tb.Width;
        string monitor = Screen.FromRectangle(tb).DeviceName;
        int dpi = (int)Native.GetDpiForWindow(taskbar);
        if (dpi <= 0) dpi = 96;
        int margin = 8 * dpi / 96;

        Rectangle? widgets = null, start = null;
        var buttons = new List<(string Id, string Class, Rectangle Bounds)>();
        try
        {
            // Все кнопки панели одним запросом (~25 мс): погода, «Пуск», поиск, приложения, значки трея.
            var root = AutomationElement.FromHandle(taskbar);
            var found = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement el in found)
            {
                var b = el.Current.BoundingRectangle;
                if (b.IsEmpty || b.Width < 1 || b.Height < 1) continue;
                buttons.Add((el.Current.AutomationId, el.Current.ClassName,
                    Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom)));
            }
            // Кнопка погоды может быть и на дополнительных панелях — зависит от версии Windows и настроек.
            widgets = buttons.Where(b => b.Id == "WidgetsButton").Select(b => (Rectangle?)b.Bounds).FirstOrDefault();
            start = buttons.Where(b => b.Id == "StartButton").Select(b => (Rectangle?)b.Bounds).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error("uia", ex);
        }

        if (start == null)
        {
            if (LastGood.TryGetValue(taskbar, out var last) && last.Taskbar == tb)
                return new TaskbarLayout(taskbar, monitor, isPrimary, tb, vertical, dpi, last.Left, last.Right, light);
            return null;
        }

        // Дальше всё считаем вдоль панели: у горизонтальной это X (слева направо), у вертикальной — Y (сверху вниз).
        int Lo(Rectangle x) => vertical ? x.Top : x.Left;
        int Hi(Rectangle x) => vertical ? x.Bottom : x.Right;
        int center = Lo(tb) + (vertical ? tb.Height : tb.Width) / 2;
        bool widgetsFirst = widgets is { } wf && Hi(wf) < center;

        // Трей (на дополнительных панелях — только часы): самая первая его кнопка во второй половине панели.
        // Окна часов на дополнительной панели Windows 11 нет — там они видны только через UI Automation.
        // Если кнопок не нашлось — окно трея, а без него — конец панели.
        int trayLo = buttons.Where(b => b.Class.StartsWith("SystemTray", StringComparison.Ordinal) && Lo(b.Bounds) > center)
            .Select(b => (int?)Lo(b.Bounds)).Min()
            ?? (Native.FindWindowEx(taskbar, IntPtr.Zero, isPrimary ? "TrayNotifyWnd" : "ClockButton", null) is var trayWnd
                && trayWnd != IntPtr.Zero && Native.GetWindowRect(trayWnd, out var tr)
                ? Lo(Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom))
                : Hi(tb));
        // Погода в конце панели (панель выровнена по левому краю) стоит перед треем.
        int rightEnd = widgets is { } wl && !widgetsFirst ? Math.Min(Lo(wl), trayLo) : trayLo;

        // Конец «Пуска», поиска, представления задач и закреплённых приложений — всё между «Пуском» и треем.
        int appsEnd = buttons
            .Where(b => b.Id != "WidgetsButton" && !b.Class.StartsWith("SystemTray", StringComparison.Ordinal)
                        && Lo(b.Bounds) >= Lo(start.Value) && Hi(b.Bounds) <= rightEnd)
            .Select(b => Hi(b.Bounds))
            .DefaultIfEmpty(Hi(start.Value))
            .Max();

        var leftZone = (Start: (widgetsFirst ? Hi(widgets!.Value) : Lo(tb)) + margin, End: Lo(start.Value) - margin);
        var rightZone = (Start: appsEnd + margin, End: rightEnd - margin);

        LastGood[taskbar] = (tb, leftZone, rightZone);
        return new TaskbarLayout(taskbar, monitor, isPrimary, tb, vertical, dpi, leftZone, rightZone, light);
    }

    private static Rectangle? FindBounds(AutomationElement root, string automationId)
    {
        var el = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (el == null) return null;
        var b = el.Current.BoundingRectangle;
        if (b.IsEmpty || b.Width < 1 || b.Height < 1) return null;
        return Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom);
    }

    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}
