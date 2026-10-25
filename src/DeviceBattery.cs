using System;
using System.Collections.Generic;

namespace BatteryBar;

internal enum DeviceKind { Mouse, Keyboard, Gamepad, Headset, Other }

internal enum ChargeState { Unknown, Discharging, Charging, Full }

/// <summary>
/// Снимок заряда одного устройства. Badge — короткая подпись у значка (PS, XB2), чтобы различать однотипные устройства.
/// </summary>
internal sealed record DeviceBattery(string Id, string Name, DeviceKind Kind, int? Percent, ChargeState State, string? Badge = null);

internal interface IBatteryProvider
{
    string Name { get; }

    /// <summary>Как часто опрашивать. Чем чаще, тем быстрее иконка реагирует на подключение и отключение.</summary>
    TimeSpan PollInterval => TimeSpan.FromSeconds(5);

    /// <summary>Опрашивает устройства. Вызывается из фонового потока.</summary>
    IEnumerable<DeviceBattery> Poll();

    /// <summary>Id устройств, про которые последний Poll точно знает, что они выключены, — их иконку убираем сразу.</summary>
    IReadOnlyCollection<string> Offline => Array.Empty<string>();

    /// <summary>
    /// Общий модуль «на всякий случай» (например, Bluetooth через Windows): его устройство не показываем,
    /// если специализированный модуль уже нашёл устройство с таким же названием.
    /// </summary>
    bool IsFallback => false;
}
