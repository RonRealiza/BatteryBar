using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryBar;

/// <summary>
/// Где на панели задач рисовать блок. Vertical — панель стоит сбоку (слева или справа).
/// Slot — свободная полоса вдоль панели (экранные физические пиксели): по X у горизонтальной, по Y у вертикальной.
/// EndAligned = прижимать содержимое к концу полосы (правому/нижнему краю), а не к началу.
/// Monitor — имя дисплея Windows (\\.\DISPLAY1), по нему запоминается выбор мониторов.
/// </summary>
internal sealed record TaskbarLayout(
    IntPtr Handle,
    string Monitor,
    bool IsPrimary,
    Rectangle Taskbar,
    bool Vertical,
    int Dpi,
    int SlotStart,
    int SlotEnd,
    bool EndAligned,
    bool LightTheme);

internal static class TaskbarLocator
{
    // UI Automation изредка не находит кнопки (панель перерисовывается) — тогда берём последнее удачное значение.
    private static readonly Dictionary<IntPtr, (Rectangle Taskbar, int Start, int End, bool EndAligned)> LastGood = new();

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
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            // Кнопка погоды может быть и на дополнительных панелях — зависит от версии Windows и настроек.
            widgets = FindBounds(root, "WidgetsButton");
            start = FindBounds(root, "StartButton");
        }
        catch (Exception ex)
        {
            Log.Error("uia", ex);
        }

        if (start == null)
        {
            if (LastGood.TryGetValue(taskbar, out var last) && last.Taskbar == tb)
                return new TaskbarLayout(taskbar, monitor, isPrimary, tb, vertical, dpi, last.Start, last.End, last.EndAligned, light);
            return null;
        }

        // Дальше всё считаем вдоль панели: у горизонтальной это X (слева направо), у вертикальной — Y (сверху вниз).
        int Lo(Rectangle x) => vertical ? x.Top : x.Left;
        int Hi(Rectangle x) => vertical ? x.Bottom : x.Right;
        int length = vertical ? tb.Height : tb.Width;
        int center = Lo(tb) + length / 2;
        // Иконки по центру, если перед «Пуском» есть свободное место (за погодой, если она в начале).
        // При выравнивании к краю «Пуск» стоит вплотную к краю панели, а погода уезжает в конец.
        int before = widgets is { } wb && Hi(wb) < center ? Hi(wb) : Lo(tb);
        bool centered = Lo(start.Value) - before > 40 * dpi / 96;
        int slotStart, slotEnd;
        bool endAligned;

        if (centered)
        {
            // Погода в начале панели — встаём сразу за ней, до кнопки «Пуск». Без погоды — от края панели.
            slotStart = widgets is { } w && Hi(w) < center ? Hi(w) + margin : Lo(tb) + margin;
            slotEnd = Lo(start.Value) - margin;
            endAligned = false;
        }
        else
        {
            // Иконки прижаты к началу — погода уезжает к трею, встаём перед ней (или перед треем/часами).
            int anchor;
            if (widgets is { } w && Lo(w) > center)
                anchor = Lo(w);
            else
            {
                var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, isPrimary ? "TrayNotifyWnd" : "ClockButton", null);
                anchor = tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr)
                    ? Lo(Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom))
                    : Hi(tb);
            }
            slotEnd = anchor - margin;
            slotStart = center;
            endAligned = true;
        }

        LastGood[taskbar] = (tb, slotStart, slotEnd, endAligned);
        return new TaskbarLayout(taskbar, monitor, isPrimary, tb, vertical, dpi, slotStart, slotEnd, endAligned, light);
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
