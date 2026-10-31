using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Enumeration;

namespace BatteryBar.Providers;

/// <summary>
/// Обычные Bluetooth-устройства (наушники, мыши, клавиатуры, геймпады), чей заряд Windows знает сама
/// и показывает в «Параметры → Bluetooth и устройства». Ничего устройствам не отправляет — только читает свойства Windows.
/// </summary>
/// <remarks>
/// Подключено ли устройство, берём из Bluetooth-«точки сопряжения» (AEP: IsConnected), а заряд — из свойства
/// DEVPKEY_Bluetooth_Battery у узлов устройства с тем же ContainerId. У самих узлов признак подключения ненадёжен.
/// </remarks>
internal sealed class BluetoothProvider : IBatteryProvider
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerKey = "System.Devices.Aep.ContainerId";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string CodMinorKey = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string AppearanceKey = "System.Devices.Aep.Bluetooth.Le.Appearance";

    private string? _lastSummary;

    public string Name => "bluetooth";

    public TimeSpan PollInterval => TimeSpan.FromSeconds(10);

    // Тот же девайс могут уже показывать специализированные модули (Xbox, DualSense, Logitech) — точнее и с зарядкой.
    public bool IsFallback => true;

    public IEnumerable<DeviceBattery> Poll()
    {
        // Готовые селекторы «только сопряжённые» отвечают сразу; самодельный запрос по протоколу
        // заставлял Windows каждый раз ~30 с сканировать эфир в поисках новых устройств.
        string[] properties = { IsConnectedKey, ContainerKey, CodMajorKey, CodMinorKey, AppearanceKey };
        var endpoints = new List<DeviceInformation>();
        foreach (string selector in new[]
                 {
                     Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            endpoints.AddRange(DeviceInformation.FindAllAsync(selector, properties, DeviceInformationKind.AssociationEndpoint)
                .AsTask().GetAwaiter().GetResult());
        }

        string summary = $"bluetooth: {endpoints.Count} paired, connected: " +
                         string.Join(", ", endpoints.Where(e => Prop<bool>(e, IsConnectedKey)).Select(e => e.Name));
        if (summary != _lastSummary)
        {
            Log.Write(summary);
            _lastSummary = summary;
        }

        var result = new List<DeviceBattery>();
        // У одного устройства бывает две точки (обычный Bluetooth и LE) с общим контейнером.
        foreach (var group in endpoints
                     .Where(e => Prop<bool>(e, IsConnectedKey))
                     .GroupBy(e => Prop<Guid>(e, ContainerKey))
                     .Where(g => g.Key != Guid.Empty))
        {
            if (ReadBattery(group.Key) is not { } percent) continue;
            var endpoint = group.First();
            var (kind, icon) = KindOf(group, endpoint.Name);
            result.Add(new DeviceBattery($"bt:{group.Key}", endpoint.Name, kind, percent, ChargeState.Unknown, Icon: icon));
        }
        return result;
    }

    private static int? ReadBattery(Guid container)
    {
        var nodes = DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                new[] { BatteryKey }, DeviceInformationKind.Device)
            .AsTask().GetAwaiter().GetResult();
        foreach (var node in nodes)
        {
            if (node.Properties.TryGetValue(BatteryKey, out var value) && value is byte b && b <= 100)
                return b;
        }
        return null;
    }

    /// <summary>
    /// Тип устройства и, если он точнее типа, значок. Bluetooth LE сообщает «внешний вид» (Appearance) из спецификации,
    /// обычный Bluetooth — класс устройства (Class of Device). Вкладыши по классу не отличить от гарнитуры —
    /// узнаём ещё и по названию.
    /// </summary>
    private static (DeviceKind Kind, DeviceIcon? Icon) KindOf(IEnumerable<DeviceInformation> endpoints, string name)
    {
        bool earbudsByName = name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Buds", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Earbud", StringComparison.OrdinalIgnoreCase);
        foreach (var e in endpoints)
        {
            // Bluetooth LE: 0x0040–0x007F телефон, 0x00C0–0x00FF часы, 0x03C1 клавиатура, 0x03C2 мышь,
            // 0x03C3–0x03C4 джойстик/геймпад, 0x0841 колонка, 0x0940–0x097F носимое аудио (0x0941 — вкладыш).
            if (e.Properties.TryGetValue(AppearanceKey, out var a) && a is ushort appearance && appearance != 0)
            {
                if (appearance == 0x03C1) return (DeviceKind.Keyboard, null);
                if (appearance == 0x03C2) return (DeviceKind.Mouse, null);
                if (appearance is 0x03C3 or 0x03C4) return (DeviceKind.Gamepad, null);
                if (appearance == 0x0941) return (DeviceKind.Headset, DeviceIcon.Earbuds);
                if (appearance is >= 0x0940 and <= 0x097F) return (DeviceKind.Headset, earbudsByName ? DeviceIcon.Earbuds : null);
                if (appearance is >= 0x0840 and <= 0x087F) return (DeviceKind.Other, DeviceIcon.Speaker);
                if (appearance is >= 0x0040 and <= 0x007F) return (DeviceKind.Other, DeviceIcon.Phone);
                if (appearance is >= 0x00C0 and <= 0x00FF) return (DeviceKind.Other, DeviceIcon.Watch);
            }

            // Обычный Bluetooth: основной класс 2 — телефон, 4 — аудио/видео, 5 — периферия, 7 — носимое.
            if (e.Properties.TryGetValue(CodMajorKey, out var mj) && mj is byte major)
            {
                byte minor = e.Properties.TryGetValue(CodMinorKey, out var mn) && mn is byte m ? m : (byte)0;
                if (major == 2) return (DeviceKind.Other, DeviceIcon.Phone);
                if (major == 4)
                {
                    // Подкласс аудио: 5 — громкоговоритель, 7 — портативное аудио (колонки тоже так называют себя).
                    if (minor is 5 or 7 && !earbudsByName) return (DeviceKind.Other, DeviceIcon.Speaker);
                    return (DeviceKind.Headset, earbudsByName ? DeviceIcon.Earbuds : null);
                }
                if (major == 5)
                {
                    if ((minor & 0x0F) is 1 or 2) return (DeviceKind.Gamepad, null);
                    if ((minor & 0x30) == 0x20) return (DeviceKind.Mouse, null);
                    if ((minor & 0x30) == 0x10) return (DeviceKind.Keyboard, null);
                }
                if (major == 7 && minor == 1) return (DeviceKind.Other, DeviceIcon.Watch);
            }
        }
        return (earbudsByName ? DeviceKind.Headset : DeviceKind.Other, earbudsByName ? DeviceIcon.Earbuds : null);
    }

    private static T? Prop<T>(DeviceInformation info, string key) =>
        info.Properties.TryGetValue(key, out var value) && value is T t ? t : default;
}
