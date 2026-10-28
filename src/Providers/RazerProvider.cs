using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HidSharp;

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
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    if (Query(stream, dev.DevicePath, 0x80) is not { } level) continue;
                    // Значение — во втором байте аргументов (arguments[1]): со сдвигом на номер отчёта это [10].
                    bool charging = Query(stream, dev.DevicePath, 0x84) is { } ch && ch[10] == 0x01;
                    answered.Add(product);

                    string name = product.Replace(" Dongle", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace(" Receiver", "", StringComparison.OrdinalIgnoreCase).Trim();
                    int percent = (int)Math.Round(level[10] * 100 / 255.0);
                    result.Add(new DeviceBattery($"razer:{dev.DevicePath}", name, KindOf(name), percent,
                        charging ? ChargeState.Charging : ChargeState.Discharging));
                }
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

    /// <summary>Команда класса 0x07 (питание). Возвращает отчёт ответа (с номером отчёта в [0]) или null.</summary>
    private byte[]? Query(HidStream stream, string path, byte commandId)
    {
        IEnumerable<byte> ids = _transaction.TryGetValue(path, out var known) ? new[] { known } : TransactionIds;
        var failures = new List<string>();
        foreach (byte tid in ids)
        {
            var tx = new byte[ReportLength + 1];
            // tx[0] — номер отчёта 0; дальше поля razer_report со смещением 1.
            tx[2] = tid;          // transaction id
            tx[6] = 0x02;         // data size
            tx[7] = 0x07;         // command class: питание
            tx[8] = commandId;    // 0x80 заряд / 0x84 зарядка
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= tx[i]; // XOR байтов 2..87 отчёта
            tx[89] = crc;
            stream.SetFeature(tx);

            string outcome = "busy";
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 20 : 50);
                var rx = new byte[ReportLength + 1];
                stream.GetFeature(rx);
                byte status = rx[1];
                if (status == StatusBusy) continue; // беспроводной ресивер ещё ждёт ответа от устройства
                if (status == StatusOk && rx[7] == 0x07 && rx[8] == commandId)
                {
                    _transaction[path] = tid;
                    _lastFailure.Remove(path);
                    return rx;
                }
                // ошибка, нет связи с устройством или не тот transaction id
                outcome = $"status {status:X2} tid {rx[2]:X2} cmd {rx[7]:X2}/{rx[8]:X2}";
                break;
            }
            failures.Add($"tid {tid:X2}: {outcome}");
        }

        if (commandId == 0x80)
        {
            string summary = string.Join("; ", failures);
            if (!_lastFailure.TryGetValue(path, out var last) || last != summary)
            {
                _lastFailure[path] = summary;
                var m = System.Text.RegularExpressions.Regex.Match(path, @"pid_[0-9a-f]{4}(&mi_[0-9a-f]{2})?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Log.Write($"razer {m.Value}: no battery answer — {summary}");
            }
        }
        return null;
    }

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
