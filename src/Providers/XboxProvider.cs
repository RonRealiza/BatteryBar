using System;
using System.Collections.Generic;
using Windows.Gaming.Input;
using Windows.System.Power;

namespace BatteryBar.Providers;

/// <summary>Геймпады Xbox через Windows.Gaming.Input (Xbox Wireless адаптер, Bluetooth, USB).</summary>
internal sealed class XboxProvider : IBatteryProvider
{
    public XboxProvider()
    {
        // Подписка заставляет Windows начать отслеживать геймпады: без неё список может оставаться пустым.
        Gamepad.GamepadAdded += (_, _) => { };
        Gamepad.GamepadRemoved += (_, _) => { };
    }

    public string Name => "xbox";

    public IEnumerable<DeviceBattery> Poll()
    {
        var found = new List<(string Name, int? Percent, ChargeState State)>();
        foreach (var pad in Gamepad.Gamepads)
        {
            var report = pad.TryGetBatteryReport();
            if (report == null || report.Status == BatteryStatus.NotPresent) continue;

            int? percent = null;
            if (report.FullChargeCapacityInMilliwattHours is int full and > 0 && report.RemainingCapacityInMilliwattHours is int rem)
                percent = Math.Clamp(rem * 100 / full, 0, 100);

            var state = report.Status switch
            {
                BatteryStatus.Charging => ChargeState.Charging,
                BatteryStatus.Discharging => ChargeState.Discharging,
                BatteryStatus.Idle => ChargeState.Full,
                _ => ChargeState.Unknown,
            };

            // По Bluetooth Windows называет геймпад «HID-compliant game controller» — даём понятное имя.
            // По нему же BatteryMonitor склеит геймпад с записью Bluetooth-модуля.
            var raw = RawGameController.FromGameController(pad);
            string name = raw?.DisplayName is { Length: > 0 } dn && dn.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
                ? dn
                : "Xbox Wireless Controller";
            found.Add((name, percent, state));
        }

        // Подпись «XB» у значка; если геймпадов несколько — с номером по порядку подключения.
        var result = new List<DeviceBattery>();
        for (int i = 0; i < found.Count; i++)
        {
            var (name, percent, state) = found[i];
            string suffix = found.Count > 1 ? $"{i + 1}" : "";
            if (suffix.Length > 0) name += $" {suffix}";
            result.Add(new DeviceBattery($"xbox:{i + 1}", name, DeviceKind.Gamepad, percent, state, $"XB{suffix}"));
        }
        return result;
    }
}
