using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BatteryBar;

/// <summary>
/// Контекстное меню иконок в трее — системное меню Windows (как у Bluetooth и других значков трея):
/// Windows 11 сама рисует его со скруглёнными углами и в цветах текущей темы.
/// </summary>
internal sealed class TrayMenu
{
    private const int AllowDark = 1;
    private const uint WM_NULL = 0x0000;

    private readonly List<(string? Text, Action? Action, bool IsDefault)> _items = new();

    /// <summary>Разрешить системным меню процесса тёмную тему. Вызвать один раз при старте.</summary>
    public static void EnableDarkMenus()
    {
        try
        {
            Native.SetPreferredAppMode(AllowDark);
            Native.FlushMenuThemes();
        }
        catch (Exception ex)
        {
            // Старые сборки Windows: меню просто останется светлым.
            Log.Error("dark menus", ex);
        }
    }

    /// <param name="isDefault">Пункт по умолчанию — выделяется жирным, как «Show Bluetooth Devices».</param>
    public void Add(string text, Action action, bool isDefault = false) => _items.Add((text, action, isDefault));

    public void AddSeparator() => _items.Add((null, null, false));

    /// <summary>Показать меню у курсора. owner — любое окно нашего процесса (нужно меню для фокуса).</summary>
    public void Show(IntPtr owner)
    {
        // Тему могли переключить, пока программа работала.
        try { Native.FlushMenuThemes(); } catch { }

        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var (text, _, isDefault) = _items[i];
                if (text == null)
                {
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                uint id = (uint)(i + 1);
                Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)id, text);
                if (isDefault) Native.SetMenuDefaultItem(menu, id, 0);
            }

            // Без этого меню не закрывается кликом мимо (известная особенность меню значков трея).
            Native.SetForegroundWindow(owner);
            var pos = Cursor.Position;
            int chosen = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON, pos.X, pos.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (chosen > 0 && chosen <= _items.Count)
                _items[chosen - 1].Action?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }
}
