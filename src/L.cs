using System.Globalization;

namespace BatteryBar;

internal enum Language { Auto, Russian, English }

/// <summary>
/// Строки интерфейса на русском и английском. В XAML — через {x:Static local:L.Имя}, поэтому класс публичный.
/// Язык применяется к окнам при их открытии; меню и подсказки читают строки каждый раз заново.
/// </summary>
public static class L
{
    /// <summary>true — английский интерфейс.</summary>
    public static bool En { get; private set; }

    internal static void Apply(Language language) => En = language switch
    {
        Language.English => true,
        Language.Russian => false,
        // «Как в системе»: русский для русской, украинской и белорусской Windows, иначе английский.
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is not ("ru" or "uk" or "be"),
    };

    private static string T(string ru, string en) => En ? en : ru;

    // Общее
    public static string Charging => T("Заряжается", "Charging");
    public static string FullyCharged => T("Полностью заряжено", "Fully charged");
    public static string LowBattery => T("Низкий заряд", "Low battery");
    public static string OnBattery => T("Работает от батареи", "On battery");
    public static string ChargingSuffix => T(" — заряжается", " — charging");
    public static string FullSuffix => T(" — заряжено", " — charged");
    public static string NoDevicesTooltip => T("BatteryBar — устройства не найдены", "BatteryBar — no devices found");
    public static string CaseLabel => T("футляр", "case");

    // Меню трея
    public static string MenuSettings => T("Настройки", "Settings");
    public static string MenuRefresh => T("Обновить заряд", "Refresh battery");
    public static string MenuExit => T("Выход", "Exit");

    // Окно настроек
    public static string SettingsTitle => T("BatteryBar — настройки", "BatteryBar — settings");
    public static string AppSubtitle => T("Заряд беспроводных устройств на панели задач Windows", "Battery levels of your wireless devices on the Windows taskbar");
    public static string DevicesHeader => T("Устройства", "Devices");
    public static string NoDevices => T("Подключённых устройств не найдено. Включите мышь, клавиатуру, гарнитуру или геймпад — они появятся здесь в течение нескольких секунд.",
        "No connected devices found. Turn on your mouse, keyboard, headset or gamepad — they will show up here within a few seconds.");
    public static string NotDetectedTitle => T("Устройство не определилось?", "Device not detected?");
    public static string NotDetectedHint => T("Отправьте отчёт об устройстве — по нему можно будет добавить поддержку", "Send a device report so support for it can be added");
    public static string DisplayHeader => T("Отображение", "Display");
    public static string WhereTitle => T("Где показывать заряд", "Where to show battery");
    public static string WhereHint => T("Блок на панели задач рядом с погодой или отдельные иконки в трее", "A block on the taskbar next to the weather, or separate tray icons");
    public static string WhereTaskbar => T("На панели задач", "On the taskbar");
    public static string WhereTray => T("В трее", "In the tray");
    public static string WhereBoth => T("На панели задач и в трее", "Taskbar and tray");
    public static string MonitorsTitle => T("Мониторы", "Monitors");
    public static string MonitorsHint => T("На панелях задач каких мониторов показывать блок", "Which monitors' taskbars show the block");
    public static string MonitorsPrimary => T("Только главный", "Primary only");
    public static string MonitorsAll => T("Все мониторы", "All monitors");
    public static string MonitorsSelected => T("Выбранные", "Selected");
    public static string Monitor => T("Монитор", "Monitor");
    public static string PrimarySuffix => T(" (главный)", " (primary)");
    public static string NoTaskbarSuffix => T(" — нет панели задач", " — no taskbar");
    public static string LanguageTitle => T("Язык", "Language");
    public static string LanguageHint => T("Язык окон, меню и подсказок BatteryBar", "Language of BatteryBar windows, menus and tooltips");
    public static string LanguageAuto => T("Как в системе", "System default");
    public static string StartupHeader => T("Запуск", "Startup");
    public static string AutostartTitle => T("Запускать вместе с Windows", "Start with Windows");
    public static string AutostartHint => T("BatteryBar будет стартовать после входа в систему", "BatteryBar will start after you sign in");
    public static string AutostartDebugHint => T("Сейчас запущена отладочная сборка — автозапуск будет указывать на неё", "A debug build is running — autostart will point to it");
    public static string MaintenanceHeader => T("Обслуживание", "Maintenance");
    public static string RefreshTitle => T("Обновить заряд", "Refresh battery");
    public static string RefreshHint => T("Опросить все устройства прямо сейчас, не дожидаясь очередного опроса", "Poll all devices right now instead of waiting for the next poll");
    public static string RefreshButton => T("Обновить сейчас", "Refresh now");
    public static string FeedbackHeader => T("Обратная связь", "Feedback");
    public static string IdeaTitle => T("Предложить улучшение", "Suggest an improvement");
    public static string IdeaHint => T("Идея или пожелание для BatteryBar — откроется заявка на GitHub", "An idea or wish for BatteryBar — opens an issue on GitHub");
    public static string IdeaIssuePrefix => T("[Идея]", "[Idea]");

    // Окно отчёта
    public static string ReportWindowTitle => T("BatteryBar — устройство не определилось", "BatteryBar — device not detected");
    public static string ReportIntro => T("Выберите устройство — по отчёту о нём можно будет добавить поддержку. Перед отправкой виден весь текст.",
        "Pick the device — its report helps add support for it. You will see the full text before sending.");
    public static string ReportDevice => T("Устройство", "Device");
    public static string ReportSearching => T("Ищем подключённые устройства…", "Looking for connected devices…");
    public static string ReportPick => T("Выберите устройство из списка подключённых", "Pick a device from the connected ones");
    public static string ReportModel => T("Модель", "Model");
    public static string ReportModelHint => T("Например, «Razer Viper V2 Pro» или «Attack Shark X3»", "For example, “Razer Viper V2 Pro” or “Attack Shark X3”");
    public static string ReportDetails => T("Подробности", "Details");
    public static string ReportDetailsHint => T("Как подключено (ресивер, Bluetooth, провод), показывает ли заряд фирменная программа производителя",
        "How it is connected (receiver, Bluetooth, cable), and whether the vendor's own app shows the battery");
    public static string ReportPreview => T("Что будет отправлено", "What will be sent");
    public static string ReportCopy => T("Скопировать отчёт", "Copy report");
    public static string ReportSend => T("Отправить на GitHub", "Send to GitHub");
    public static string ReportCopied => T("Отчёт скопирован — можно вставить в письмо или сообщение", "Report copied — you can paste it into an email or message");
    public static string ReportIssuePrefix => T("[Устройство]", "[Device]");
    public static string ReportPasteHere => T("Отчёт скопирован в буфер обмена — вставьте его сюда (Ctrl+V).", "The report was copied to the clipboard — paste it here (Ctrl+V).");
    public static string ReportTooLong => T("Отчёт длинный и скопирован в буфер — вставьте его в заявку (Ctrl+V)", "The report is long and was copied — paste it into the issue (Ctrl+V)");
    public static string ReportOpening => T("Открываю GitHub — проверьте заявку и нажмите «Create»", "Opening GitHub — check the issue and press “Create”");
    public static string ReportUnnamed => T("Без названия", "Unnamed");
    public static string ReportConnected => T("подключено", "connected");
}
