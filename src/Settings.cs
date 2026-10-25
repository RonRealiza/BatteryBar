using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryBar;

internal enum MonitorMode { Primary, All, Selected }

internal enum DisplayMode { Taskbar, Tray, Both }

/// <summary>Настройки пользователя в %LOCALAPPDATA%\BatteryBar\settings.json.</summary>
internal sealed class Settings
{
    private static readonly string FilePath = Path.Combine(Log.Directory, "settings.json");

    public Language Language { get; set; } = Language.Auto;

    /// <summary>Проверять обновления на GitHub автоматически (после запуска и раз в 12 часов).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public DisplayMode Display { get; set; } = DisplayMode.Taskbar;

    [JsonIgnore] public bool ShowOnTaskbar => Display != DisplayMode.Tray;

    [JsonIgnore] public bool ShowInTray => Display != DisplayMode.Taskbar;

    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;

    /// <summary>Имена дисплеев (\\.\DISPLAY2) для режима Selected.</summary>
    public List<string> SelectedMonitors { get; set; } = new();

    /// <summary>Какие панели задач показывать. Если выбранных мониторов сейчас нет — показываем на главном.</summary>
    public List<TaskbarLayout> Filter(IReadOnlyList<TaskbarLayout> taskbars)
    {
        var shown = Monitors switch
        {
            MonitorMode.All => taskbars.ToList(),
            MonitorMode.Selected => taskbars.Where(t => SelectedMonitors.Contains(t.Monitor)).ToList(),
            _ => taskbars.Where(t => t.IsPrimary).ToList(),
        };
        return shown.Count > 0 ? shown : taskbars.Where(t => t.IsPrimary).ToList();
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("settings save", ex);
        }
    }
}
