using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBar;

/// <summary>
/// По иконке в трее на каждое устройство: процент цифрами, название — в подсказке.
/// Работаем напрямую через Shell_NotifyIcon, а не NotifyIcon: у каждой иконки постоянный uID (из Id устройства),
/// чтобы Windows запоминала, что пользователь вынес её из скрытой области трея.
/// </summary>
internal sealed class DeviceTrayIcons : NativeWindow, IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private readonly TrayMenu _menu;
    private readonly Action _onClick;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<string, (uint Uid, IntPtr Icon, string Key)> _icons = new();
    private IReadOnlyList<DeviceBattery> _devices = Array.Empty<DeviceBattery>();

    /// <param name="menu">Меню по правому клику.</param>
    /// <param name="onClick">Действие по левому клику (открыть настройки).</param>
    public DeviceTrayIcons(TrayMenu menu, Action onClick)
    {
        _menu = menu;
        _onClick = onClick;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        // Обычное скрытое окно верхнего уровня: только такие получают широковещательное TaskbarCreated.
        CreateHandle(new CreateParams { Caption = "BatteryBar.Tray" });
    }

    public void Update(IReadOnlyList<DeviceBattery> devices)
    {
        _devices = devices;
        bool light = TaskbarLocator.IsLightTheme();
        var wanted = devices.Select(d => d.Id).ToHashSet();

        foreach (var id in _icons.Keys.Where(id => !wanted.Contains(id)).ToList())
            Remove(id);

        foreach (var d in devices)
        {
            string key = $"{d.Percent}|{d.State}|{d.Name}|{light}";
            if (_icons.TryGetValue(d.Id, out var existing) && existing.Key == key) continue;

            IntPtr icon = RenderIcon(d, light);
            var data = Data(existing.Uid != 0 ? existing.Uid : UidFor(d.Id), icon, Tooltip(d));
            bool added = existing.Uid != 0 && Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
            if (!added) Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);

            if (existing.Icon != IntPtr.Zero) Native.DestroyIcon(existing.Icon);
            _icons[d.Id] = (data.uID, icon, key);
        }
    }

    private void Remove(string id)
    {
        var (uid, icon, _) = _icons[id];
        var data = Data(uid, IntPtr.Zero, "");
        data.uFlags = 0;
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        _icons.Remove(id);
    }

    private Native.NOTIFYICONDATA Data(uint uid, IntPtr icon, string tip) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = uid,
        uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip.Length > 127 ? tip[..127] : tip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Постоянный номер иконки по Id устройства (FNV-1a), чтобы он не зависел от порядка подключения.</summary>
    private static uint UidFor(string id)
    {
        uint hash = 2166136261;
        foreach (char c in id)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return 1000 + hash % 1_000_000;
    }

    private static string Tooltip(DeviceBattery d)
    {
        string percent = d.Percent is { } p ? $"{p}%" : "?";
        string state = d.State switch
        {
            ChargeState.Charging => " — заряжается",
            ChargeState.Full => " — заряжено",
            _ => "",
        };
        return $"{d.Name}: {percent}{state}";
    }

    private static IntPtr RenderIcon(DeviceBattery d, bool light)
    {
        int size = SystemInformation.SmallIconSize.Width;
        string text = d.Percent is { } p ? p.ToString() : "?";

        var fg = light ? Color.FromArgb(255, 0, 0, 0) : Color.White;
        var color = d.State == ChargeState.Charging
            ? (light ? Color.FromArgb(255, 15, 123, 15) : Color.FromArgb(255, 108, 203, 95))
            : d.Percent is <= 20
                ? (light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 120, 130))
                : fg;

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fmt = StringFormat.GenericTypographic;

            // Подбираем шрифт так, чтобы число (до «100») заняло всю ширину иконки.
            float px = size * 0.9f;
            Font font = new("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font, PointF.Empty, fmt);
            while (sz.Width > size + 1 && px > 6)
            {
                font.Dispose();
                px -= 0.5f;
                font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                sz = g.MeasureString(text, font, PointF.Empty, fmt);
            }
            using (font)
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, (size - sz.Width) / 2f, (size - sz.Height) / 2f, fmt);
        }
        return bmp.GetHicon();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)_taskbarCreated)
        {
            // Проводник перезапустился — иконки пропали, добавляем заново.
            foreach (var id in _icons.Keys.ToList())
            {
                Native.DestroyIcon(_icons[id].Icon);
                _icons.Remove(id);
            }
            Update(_devices);
        }
        else if (m.Msg == CallbackMessage && (int)m.LParam == WM_RBUTTONUP)
        {
            _menu.Show(Handle);
        }
        else if (m.Msg == CallbackMessage && (int)m.LParam == WM_LBUTTONUP)
        {
            _onClick();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _icons.Keys.ToList())
            Remove(id);
        DestroyHandle();
    }
}
