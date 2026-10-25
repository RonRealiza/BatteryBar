# BatteryBar

Показывает заряд беспроводных устройств на панели задач Windows 11 (снизу или сбоку, на одном или нескольких мониторах) и/или отдельными иконками в трее. Интерфейс на русском и английском (по умолчанию — как в системе).

*Shows battery levels of wireless devices on the Windows 11 taskbar and/or in the tray. The UI is available in English and Russian.*

## Поддерживаемые устройства

| Устройства | Как читается | Проверено на железе |
|---|---|---|
| Мыши и клавиатуры Logitech через Lightspeed / Unifying / Bolt и по проводу | HID++ 2.0, фичи 0x1004 / 0x1000 / 0x1001 / 0x1F20 | Pro X Superlight 2 |
| Гарнитуры Logitech G с HID++ (G535, G733, Pro X…) | HID++ 0x1F20 (напряжение) | нет |
| Геймпады Xbox (адаптер, Bluetooth, USB) | Windows.Gaming.Input | да |
| DualSense / DualSense Edge (USB, Bluetooth) | байт статуса во входном отчёте | да |
| DualShock 4 (обе ревизии и USB-адаптер) | байт статуса во входном отчёте | нет |
| Bluetooth-устройства, чей заряд показывает сама Windows | свойство DEVPKEY_Bluetooth_Battery | нет |
| HyperX Cloud III Wireless | отчёт 0x66, протокол из HyperHeadset | да |
| SteelSeries Arctis Nova 7 / 7P / 7X, Nova 5, Nova Pro Wireless, Arctis 7 / 7+ / 1 / 9 / Pro | протоколы из HeadsetControl | нет |
| HyperX Cloud Alpha Wireless, Cloud II Wireless, Cloud Flight | протоколы из HeadsetControl | нет |
| Corsair Void и родственные | протокол из HeadsetControl | нет |
| Клавиатура Zornher ZH870 (2.4 ГГц) | команда 0x20 через интерфейс MI_03 | да |
| AirPods и Beats | Bluetooth-реклама Apple (Proximity Pairing), только пока они подключены | AirPods Pro 2 |
| Беспроводные мыши, клавиатуры и гарнитуры Razer | feature-отчёт 90 байт, команды 0x07/0x80 и 0x07/0x84 (по OpenRazer) | нет |

Протоколы взяты из описаний открытых проектов [HeadsetControl](https://github.com/Sapd/HeadsetControl), [HyperHeadset](https://github.com/LennardKittner/HyperHeadset), [OpenRazer](https://github.com/openrazer/openrazer) и [OpenPods](https://github.com/adolfintel/OpenPods); код написан заново.

**Правило безопасности:** команды отправляются только устройствам, точно опознанным по VID/PID (и, где нужно, по названию и интерфейсу). Остальные устройства BatteryBar не трогает.

Не поддерживаются модели с закрытыми и неописанными протоколами — их можно добавить по инструкции ниже.

## Сборка и запуск

Нужен [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet run
```

Установленная копия живёт в `%LOCALAPPDATA%\Programs\BatteryBar`, на неё же указывает автозапуск. Пересобрать и обновить её (приложение перезапустится само):

```bash
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

Клик по иконке BatteryBar в трее открывает окно настроек: режим отображения, мониторы, автозапуск, журнал.
Журнал: `%LOCALAPPDATA%\BatteryBar\log.txt`.
Список всех HID-устройств (для добавления новых моделей): `BatteryBar.exe --list-hid` → `%LOCALAPPDATA%\BatteryBar\hid-devices.txt`.

## Как устроено

- `src/TaskbarLocator.cs` — через UI Automation находит кнопки `WidgetsButton` и `StartButton` на каждой панели задач и вычисляет свободное место между ними (вдоль горизонтальной или вертикальной панели).
- `src/OverlayWindow.cs` — прозрачное дочернее окно панели задач (`UpdateLayeredWindow`), не перехватывает мышь и фокус.
- `src/DeviceTrayIcons.cs` — иконки устройств в трее с постоянными uID.
- `src/SettingsWindow.xaml` — окно настроек на WPF с темой Fluent.
- `src/Providers/*` — по модулю на семейство устройств; `BatteryMonitor` опрашивает их (обычно раз в 5 секунд) и убирает дубли.

## Как добавить новую модель

1. Запустите `BatteryBar.exe --list-hid` и найдите устройство по названию: VID, PID и интерфейсы (`mi_xx`).
2. Если у производителя есть программа, показывающая заряд, — послушайте её ответы на служебных интерфейсах (пакеты приходят всем, кто читает канал) или запишите трафик Wireshark + USBPcap.
3. Добавьте модель в подходящий модуль, например строку в таблицу `Models` в `src/Providers/HeadsetsProvider.cs`.
