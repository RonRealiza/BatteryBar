using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace BatteryBar.Providers;

/// <summary>
/// Клавиатуры AULA (F75, F87, AU75…) через 2.4 ГГц ресивер CompX 3554:FA09. Протокол — по открытым утилитам
/// для этих клавиатур (aula-f87-control, sanshoku): коллекция производителя 0xFF02, отчёт 0x13 на 20 байт,
/// байт 1 — команда, байт 19 — сумма байтов 0..18. Команда 0x4A возвращает заряд в байте 5 и состояние в байте 6.
/// </summary>
internal sealed class AulaProvider : IBatteryProvider
{
    private const int VendorId = 0x3554;
    private const int ProductId = 0xFA09;
    private const ushort VendorPage = 0xFF02;
    private const int PacketLength = 20;
    private const byte ReportId = 0x13;
    private const byte CmdBattery = 0x4A;
    private const byte StateBattery = 0x01, StateCable = 0x10;

    // С кабелем клавиатура вместо заряда шлёт 100 — показываем последний заряд от батареи.
    private int? _lastLevel;
    private string? _lastRaw;

    public string Name => "aula";

    // Спящая клавиатура не отвечает, и каждый опрос тогда ждёт ответа почти секунду — чаще не нужно.
    public TimeSpan PollInterval => TimeSpan.FromSeconds(15);

    public IEnumerable<DeviceBattery> Poll()
    {
        var result = new List<DeviceBattery>();
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId, ProductId).Where(IsVendorInterface))
        {
            try
            {
                if (!dev.TryOpen(out var stream)) continue;
                using (stream)
                {
                    // Первый вопрос после сна клавиатуры иногда остаётся без ответа — переспрашиваем один раз.
                    var r = Request(stream) ?? Request(stream);
                    string raw = r == null ? "no reply" : string.Join(" ", r.Take(8).Select(b => b.ToString("X2")));
                    if (raw != _lastRaw)
                    {
                        Log.Write($"aula raw: {raw}");
                        _lastRaw = raw;
                    }
                    // Нет ответа — клавиатура спит или выключена; BatteryMonitor покажет последнее значение.
                    if (r == null) continue;

                    int level = r[5];
                    if (r[6] == StateCable)
                    {
                        result.Add(new DeviceBattery("aula:keyboard", "AULA", DeviceKind.Keyboard, _lastLevel, ChargeState.Charging));
                    }
                    else if (r[6] == StateBattery && level is >= 1 and <= 100)
                    {
                        _lastLevel = level;
                        result.Add(new DeviceBattery("aula:keyboard", "AULA", DeviceKind.Keyboard, level, ChargeState.Discharging));
                    }
                    break; // у ресивера одна клавиатура, а коллекций 0xFF02 бывает несколько
                }
            }
            catch (Exception ex)
            {
                Log.Error("aula", ex);
            }
        }
        return result;
    }

    /// <summary>Коллекция производителя 0xFF02 с отчётами по 20 байт (с номером отчёта).</summary>
    private static bool IsVendorInterface(HidDevice d)
    {
        try
        {
            if (d.GetMaxOutputReportLength() != PacketLength || d.GetMaxInputReportLength() != PacketLength) return false;
            return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u >> 16 == VendorPage));
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? Request(HidStream stream)
    {
        // Ответы на прошлые вопросы и нажатия клавиш из этой же коллекции нам не нужны.
        var rx = new byte[PacketLength];
        stream.ReadTimeout = 1;
        try { while (stream.Read(rx, 0, rx.Length) > 0) { } }
        catch (TimeoutException) { }

        var tx = new byte[PacketLength];
        tx[0] = ReportId;
        tx[1] = CmdBattery;
        tx[PacketLength - 1] = Checksum(tx);
        stream.WriteTimeout = 300;
        stream.Write(tx);

        stream.ReadTimeout = 100;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            int n;
            try { n = stream.Read(rx, 0, rx.Length); }
            catch (TimeoutException) { continue; }
            // В ответе у команды бывает выставлен старший бит.
            if (n == PacketLength && rx[0] == ReportId && (rx[1] & 0x7F) == CmdBattery && rx[PacketLength - 1] == Checksum(rx))
                return rx;
        }
        return null;
    }

    private static byte Checksum(byte[] packet)
    {
        int sum = 0;
        for (int i = 0; i < PacketLength - 1; i++) sum += packet[i];
        return (byte)sum;
    }
}
