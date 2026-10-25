# Разработка BatteryBar

[← README](../README.ru.md)

## Сборка и запуск

Нужен [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet run
```

Собрать Release и обновить установленную копию в `%LOCALAPPDATA%\Programs\BatteryBar` (на неё указывает автозапуск; запущенное приложение перезапустится):

```bash
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

Перерисовать иконку приложения (`assets/BatteryBar.ico`): `tools\make-icon.ps1`.

### Ключи запуска

| Ключ | Что делает |
|---|---|
| `--settings` | сразу открыть окно настроек |
| `--report` | сразу открыть окно «Устройство не определилось?» |
| `--menu` | сразу открыть меню трея у курсора |
| `--list-hid` | записать все HID-устройства в `%LOCALAPPDATA%\BatteryBar\hid-devices.txt` и выйти |

Журнал: `%LOCALAPPDATA%\BatteryBar\log.txt`. Настройки: `settings.json` там же.

## Как устроено

| Файл | Назначение |
|---|---|
| `src/TrayApp.cs` | точка сборки: иконка в трее, меню, окна, опрос, отрисовка |
| `src/BatteryMonitor.cs` | опрос модулей в фоне, сглаживание пропусков, скрытие выключенных устройств, дубли |
| `src/TaskbarLocator.cs` | находит панели задач и через UI Automation — кнопки `WidgetsButton` и `StartButton`, считает свободное место (горизонтальная и вертикальная панель) |
| `src/OverlayWindow.cs` | прозрачное дочернее окно панели задач (`UpdateLayeredWindow`), не перехватывает мышь и фокус |
| `src/DeviceTrayIcons.cs` | иконки устройств в трее с постоянными uID |
| `src/TrayMenu.cs` | меню трея (WPF ContextMenu с темой Fluent) |
| `src/SettingsWindow.xaml`, `src/ReportWindow.xaml` | окна настроек и отчёта (WPF, тема Fluent .NET 9) |
| `src/DeviceReport.cs` | сбор обезличенного отчёта и ссылка на заявку GitHub |
| `src/L.cs` | строки интерфейса на русском и английском |
| `src/Providers/*` | по модулю на семейство устройств |

### Модули устройств

| Модуль | Протокол |
|---|---|
| `LogitechProvider` | HID++ 2.0: фичи 0x1004 / 0x1000 / 0x1001 / 0x1F20; ошибка «недоступно» приходит короткими отчётами — слушаем обе коллекции |
| `RazerProvider` | feature-отчёт 90 байт, команды 0x07/0x80 и 0x07/0x84 (по OpenRazer) |
| `HeadsetsProvider` | таблица моделей SteelSeries / HyperX / Corsair (по HeadsetControl) |
| `HyperXProvider` | HyperX Cloud III Wireless, отчёт 0x66 (по HyperHeadset) |
| `ZornherProvider` | ZH870: команда 0x20 через MI_03; зарядка распознаётся по устойчивому скачку процента |
| `DualSenseProvider`, `DualShock4Provider` | байт статуса во входном отчёте (по драйверу Linux hid-playstation) |
| `XboxProvider` | Windows.Gaming.Input |
| `AirPodsProvider` | Bluetooth-реклама Apple (Proximity Pairing, по OpenPods), только пока AirPods подключены |
| `BluetoothProvider` | свойство Windows `DEVPKEY_Bluetooth_Battery`; общий модуль, уступает специализированным |

Важно: HeadsetControl работает через hidapi, которая на Windows отбрасывает нулевой номер отчёта в начале ответа, а HidSharp его оставляет — у моделей без номера отчёта индексы байтов ответа здесь на 1 больше.

### Правило безопасности

Команды отправляются только устройствам, точно опознанным по VID/PID (и, где нужно, по названию и интерфейсу), и только команды чтения. Остальные устройства BatteryBar не трогает.

## Как добавить новое устройство

1. Узнайте VID, PID и интерфейсы: `BatteryBar.exe --list-hid` или отчёт из окна «Устройство не определилось?».
2. Если у производителя есть программа, показывающая заряд, — послушайте ответы на служебных интерфейсах (входные отчёты приходят всем, кто читает канал) или запишите трафик через Wireshark + USBPcap.
3. Добавьте модель в подходящий модуль, например строку в таблицу `Models` в `src/Providers/HeadsetsProvider.cs`, или новый модуль, реализующий `IBatteryProvider`, и зарегистрируйте его в `TrayApp`.
4. Проверьте на устройстве: заряд, зарядку, выключение и сон.
