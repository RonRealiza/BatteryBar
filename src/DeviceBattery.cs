using System;
using System.Collections.Generic;

namespace BatteryBar;

internal enum DeviceKind { Mouse, Keyboard, Gamepad, Headset, Other }

internal enum ChargeState { Unknown, Discharging, Charging, Full }

/// <summary>Значок устройства. Порядок — как в меню выбора значка в настройках.</summary>
internal enum DeviceIcon { Keyboard, Mouse, Headset, Earbuds, Speaker, Gamepad, PlayStation, Xbox, Phone, Watch, Other }

/// <summary>
/// Снимок заряда одного устройства. Badge — короткая подпись у значка (PS, XB2), чтобы различать однотипные устройства.
/// Icon — значок, если он точнее типа (вкладыши, колонка) или его выбрал пользователь; иначе — по типу.
/// </summary>
internal sealed record DeviceBattery(string Id, string Name, DeviceKind Kind, int? Percent, ChargeState State, string? Badge = null,
    DeviceIcon? Icon = null);

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
