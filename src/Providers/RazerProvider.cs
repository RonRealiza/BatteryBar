using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar.Providers;

/// <summary>
/// Беспроводные мыши, клавиатуры и гарнитуры Razer. Протокол — по описанию драйвера OpenRazer:
/// feature-отчёт на 90 байт (статус, transaction id, …, класс и номер команды, 80 байт аргументов, XOR-сумма).
/// Отправляем только команды чтения: 0x07/0x80 — заряд (0–255), 0x07/0x84 — идёт ли зарядка.
/// </summary>
internal sealed class RazerProvider : IBatteryProvider
{
    private const int VendorId = 0x1532;
    private const int ReportLength = 90;
    private const byte StatusBusy = 0x01, StatusOk = 0x02;

    // Разные модели ждут разный transaction id; найденный для устройства запоминаем.
    private static readonly byte[] TransactionIds = { 0x1F, 0x3F, 0xFF };
    private readonly Dictionary<string, byte> _transaction = new();

    // Почему устройство не ответило на запрос заряда — пишем в журнал, только когда причина меняется.
    private readonly Dictionary<string, string> _lastFailure = new();

    public string Name => "razer";

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        var answered = new HashSet<string>(); // у устройства бывает несколько подходящих коллекций
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId).Where(d => !IsAccessory(d.ProductID) && HasControlReport(d)))
        {
            string product;
            try { product = dev.GetProductName(); }
            catch { product = "Razer"; }
            if (answered.Contains(product)) continue;

            try
            {
                // Служебный отчёт у мышей Razer — в коллекции самой мыши (mi_00), её открываем без прав доступа.
                using var handle = Native.CreateFile(dev.DevicePath, 0, Native.FILE_SHARE_READ_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    LogFailure(dev.DevicePath, $"cannot open (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                    continue;
                }
                if (Query(handle, dev.DevicePath, 0x80) is not { } level) continue;
                // Значение — во втором байте аргументов (arguments[1]): со сдвигом на номер отчёта это [10].
                bool charging = Query(handle, dev.DevicePath, 0x84) is { } ch && ch[10] == 0x01;
                answered.Add(product);

                string name = product.Replace(" Dongle", "", StringComparison.OrdinalIgnoreCase)
                                     .Replace(" Receiver", "", StringComparison.OrdinalIgnoreCase).Trim();
                // Док называет себя («Razer Mouse Dock Pro») — спрашиваем, какая мышь к нему подключена.
                if (product.Contains("Dock", StringComparison.OrdinalIgnoreCase) && DockedMouse(handle, dev.DevicePath) is { } mouse)
                    name = mouse;
                int percent = (int)Math.Round(level[10] * 100 / 255.0);
                result.Add(new DeviceBattery($"razer:{dev.DevicePath}", name, KindOf(name), percent,
                    charging ? ChargeState.Charging : ChargeState.Discharging));
            }
            catch (Exception ex)
            {
                Log.Error($"razer {dev.ProductID:X4}", ex);
            }
        }
        return result;
    }

    /// <summary>
    /// Коды 0x0Fxx у Razer — аксессуары без батареи (контроллер ARGB-подсветки, коврики, подставки, Chroma-доки).
    /// Некоторые из них «успешно» отвечают на запрос заряда нулём.
    /// </summary>
    private static bool IsAccessory(int productId) => (productId & 0xFF00) == 0x0F00;

    /// <summary>То же по Id устройства («razer:» + путь с pid_xxxx) — для записей, сохранённых прошлыми версиями.</summary>
    public static bool IsAccessoryId(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"pid_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return id.StartsWith("razer:", StringComparison.Ordinal) && m.Success
               && IsAccessory(Convert.ToInt32(m.Groups[1].Value, 16));
    }

    /// <summary>Служебная коллекция Razer: feature-отчёт ровно на 90 байт (+ номер отчёта).</summary>
    private static bool HasControlReport(HidDevice d)
    {
        try { return d.GetMaxFeatureReportLength() == ReportLength + 1; }
        catch { return false; }
    }

    /// <summary>
    /// Один обмен: отправить команду и дождаться ответа на неё. Возвращает отчёт ответа (с номером отчёта в [0])
    /// или null, тогда в outcome — почему.
    /// </summary>
    private static byte[]? Exchange(SafeFileHandle handle, byte tid, byte commandClass, byte commandId, byte dataSize, out string outcome)
    {
        var tx = new byte[ReportLength + 1];
        // tx[0] — номер отчёта 0; дальше поля razer_report со смещением 1.
        tx[2] = tid;           // transaction id
        tx[6] = dataSize;      // data size
        tx[7] = commandClass;  // command class
        tx[8] = commandId;     // command id
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= tx[i]; // XOR байтов 2..87 отчёта
        tx[89] = crc;
        if (!Native.HidD_SetFeature(handle, tx, tx.Length))
        {
            outcome = $"send error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return null;
        }

        outcome = "busy";
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 20 : 50);
            var rx = new byte[ReportLength + 1];
            if (!Native.HidD_GetFeature(handle, rx, rx.Length))
            {
                outcome = $"read error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
                return null;
            }
            byte status = rx[1];
            if (status == StatusBusy) continue; // беспроводной ресивер ещё ждёт ответа от устройства
            if (status == StatusOk && rx[7] == commandClass && rx[8] == commandId) return rx;
            // ошибка, нет связи с устройством или не тот transaction id
            outcome = $"status {status:X2} tid {rx[2]:X2} cmd {rx[7]:X2}/{rx[8]:X2}";
            return null;
        }
        return null;
    }

    /// <summary>Команда класса 0x07 (питание). Возвращает отчёт ответа (с номером отчёта в [0]) или null.</summary>
    private byte[]? Query(SafeFileHandle handle, string path, byte commandId)
    {
        IEnumerable<byte> ids = _transaction.TryGetValue(path, out var known) ? new[] { known } : TransactionIds;
        var failures = new List<string>();
        foreach (byte tid in ids)
        {
            // 0x80 заряд / 0x84 зарядка
            if (Exchange(handle, tid, 0x07, commandId, 0x02, out string outcome) is { } rx)
            {
                _transaction[path] = tid;
                _lastFailure.Remove(path);
                return rx;
            }
            failures.Add($"tid {tid:X2}: {outcome}");
        }

        if (commandId == 0x80)
            LogFailure(path, $"no battery answer — {string.Join("; ", failures)}");
        return null;
    }

    private void LogFailure(string path, string reason)
    {
        if (_lastFailure.TryGetValue(path, out var last) && last == reason) return;
        _lastFailure[path] = reason;
        var m = System.Text.RegularExpressions.Regex.Match(path, @"pid_[0-9a-f]{4}(&mi_[0-9a-f]{2})?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Log.Write($"razer {m.Value}: {reason}");
    }

    /// <summary>
    /// Какая мышь подключена к Mouse Dock Pro. Док отдаёт таблицу слотов (класс 0x00, команда 0xBF, transaction id 0x3F):
    /// arguments[0] — число слотов, дальше по 3 байта на слот — флаг и код мыши (PID, старший байт первым).
    /// Формат — по разбору протокола в OpenRazer (PR #2817), на нашей стороне ещё не проверен на настоящем доке,
    /// поэтому ответ пишем в журнал, а незнакомый код просто не используем.
    /// </summary>
    private string? DockedMouse(SafeFileHandle handle, string path)
    {
        var rx = Exchange(handle, 0x3F, 0x00, 0xBF, 0x1F, out string outcome);
        string raw = rx == null ? outcome : string.Join(" ", rx.Skip(9).Take(1 + 3 * Math.Min((int)rx[9], 4)).Select(b => b.ToString("X2")));
        if (!_lastDockAnswer.TryGetValue(path, out var last) || last != raw)
        {
            _lastDockAnswer[path] = raw;
            Log.Write($"razer dock slots: {raw}");
        }
        if (rx == null) return null;

        int count = Math.Min((int)rx[9], 26);
        for (int i = 0; i < count; i++)
        {
            int at = 10 + 3 * i;
            int pid = rx[at + 1] << 8 | rx[at + 2];
            if (pid is 0 or 0xFFFF) continue;
            if (DockMice.TryGetValue(pid, out var name)) return name;
            if (_unknownDockPid.Add(pid)) Log.Write($"razer dock: unknown mouse pid {pid:X4}");
        }
        return null;
    }

    private readonly Dictionary<string, string> _lastDockAnswer = new();
    private readonly HashSet<int> _unknownDockPid = new();

    /// <summary>
    /// Мыши, которые работают с Mouse Dock Pro: коды по проводу и по радио (док может сообщать любой из них).
    /// По списку устройств OpenRazer.
    /// </summary>
    private static readonly Dictionary<int, string> DockMice = new()
    {
        [0x00AA] = "Razer Basilisk V3 Pro", [0x00AB] = "Razer Basilisk V3 Pro",
        [0x00CC] = "Razer Basilisk V3 Pro 35K", [0x00CD] = "Razer Basilisk V3 Pro 35K",
        [0x00AF] = "Razer Cobra Pro", [0x00B0] = "Razer Cobra Pro",
        [0x00A7] = "Razer Naga V2 Pro", [0x00A8] = "Razer Naga V2 Pro",
        [0x00B6] = "Razer DeathAdder V3 Pro", [0x00B7] = "Razer DeathAdder V3 Pro",
        [0x00A5] = "Razer Viper V2 Pro", [0x00A6] = "Razer Viper V2 Pro",
        [0x00C0] = "Razer Viper V3 Pro", [0x00C1] = "Razer Viper V3 Pro",
    };

    private static DeviceKind KindOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("blackwidow") || n.Contains("huntsman") || n.Contains("deathstalker") || n.Contains("ornata") || n.Contains("keyboard"))
            return DeviceKind.Keyboard;
        if (n.Contains("kraken") || n.Contains("barracuda") || n.Contains("blackshark") || n.Contains("nari") || n.Contains("headset"))
            return DeviceKind.Headset;
        if (n.Contains("wolverine") || n.Contains("kishi") || n.Contains("raiju") || n.Contains("controller"))
            return DeviceKind.Gamepad;
        return DeviceKind.Mouse;
    }
}
