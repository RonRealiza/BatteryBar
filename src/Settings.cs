using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatteryBar;

internal enum MonitorMode { Primary, All, Selected }

internal enum DisplayMode { Taskbar, Tray, Both }

/// <summary>Чем разделять устройства на панели задач.</summary>
internal enum SeparatorStyle { Gap, Line }

/// <summary>
/// Где на панели задач стоит блок. Слева от «Пуска»: сразу за погодой, посередине или вплотную к «Пуску».
/// Справа: сразу за приложениями, посередине или вплотную к трею. Порядок — как в списке настроек.
/// </summary>
internal enum TaskbarPosition { AfterWidgets, CenterLeft, BeforeStart, AfterApps, CenterRight, BeforeTray }

/// <summary>Настройки пользователя в %LOCALAPPDATA%\BatteryBar\settings.json.</summary>
internal sealed class Settings
{
    private static readonly string FilePath = Path.Combine(Log.Directory, "settings.json");

    public Language Language { get; set; } = Language.Auto;

    /// <summary>Проверять обновления на GitHub автоматически (после запуска и раз в 12 часов).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public DisplayMode Display { get; set; } = DisplayMode.Taskbar;

    /// <summary>Скрыть значок самой программы в трее (настройки тогда открываются повторным запуском BatteryBar).</summary>
    public bool HideAppIcon { get; set; }

    [JsonIgnore] public bool ShowOnTaskbar => Display != DisplayMode.Tray;

    [JsonIgnore] public bool ShowInTray => Display != DisplayMode.Taskbar;

    public MonitorMode Monitors { get; set; } = MonitorMode.Primary;

    public SeparatorStyle Separator { get; set; } = SeparatorStyle.Gap;

    public TaskbarPosition Position { get; set; } = TaskbarPosition.AfterWidgets;

    /// <summary>Дополнительный отступ (пиксели при 100% масштабе) от погоды — для положения «справа от погоды».</summary>
    public int OffsetFromWidgets { get; set; }

    /// <summary>Дополнительный отступ от трея — для положения «слева от трея».</summary>
    public int OffsetFromTray { get; set; }

    /// <summary>Отступ для текущего положения: у остальных положений его нет.</summary>
    [JsonIgnore]
    public int EdgeOffset => Position switch
    {
        TaskbarPosition.AfterWidgets => OffsetFromWidgets,
        TaskbarPosition.BeforeTray => OffsetFromTray,
        _ => 0,
    };

    /// <summary>
    /// Порядок устройств, который пользователь задал перетаскиванием (Id устройств). Устройства не из списка
    /// идут после, в обычном порядке: клавиатуры, мыши, гарнитуры, геймпады.
    /// </summary>
    public List<string> DeviceOrder { get; set; } = new();

    /// <summary>Свои названия устройств (Id → название), например «Cobra Pro» вместо «Razer Mouse Dock Pro».</summary>
    public Dictionary<string, string> CustomNames { get; set; } = new();

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
