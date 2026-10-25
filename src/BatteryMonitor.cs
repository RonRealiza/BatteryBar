using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace BatteryBar;

/// <summary>Периодически опрашивает все провайдеры в фоновом потоке.</summary>
internal sealed class BatteryMonitor : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    // Обычное устройство убираем после стольких пропущенных опросов подряд (один пропуск — случайный сбой).
    private const int MissesToHide = 2;

    // Выключенную мышь Logitech ресивер опознаёт явно (провайдер сообщает Offline), а если ответа нет вовсе —
    // держим последнее значение долго и сохраняем его на диск.
    private static readonly TimeSpan StickyStaleAfter = TimeSpan.FromHours(12);
    private static readonly string StatePath = Path.Combine(Log.Directory, "last-seen.json");

    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Dictionary<string, (DeviceBattery Device, DateTime Seen)> _lastSeen = new();
    private readonly Dictionary<string, IBatteryProvider> _owner = new();
    private readonly Dictionary<string, int> _misses = new();
    private readonly Dictionary<IBatteryProvider, DateTime> _lastPoll = new();
    private volatile bool _forceAll;
    private volatile bool _stopped;
    private string? _lastSummary;
    private string? _savedState;

    public event Action<IReadOnlyList<DeviceBattery>>? Updated;

    public BatteryMonitor(IReadOnlyList<IBatteryProvider> providers)
    {
        _providers = providers;
        _thread = new Thread(Run) { IsBackground = true, Name = "BatteryMonitor" };
    }

    public void Start() => _thread.Start();

    public void RefreshNow()
    {
        _forceAll = true;
        _wake.Set();
    }

    // Мыши Logitech и Razer засыпают, и ресивер их не видит — держим последнее значение, пока не ответят.
    private static bool IsSticky(string id) =>
        id.StartsWith("logi:", StringComparison.Ordinal) || id.StartsWith("razer:", StringComparison.Ordinal);

    private void Run()
    {
        LoadState();
        if (_lastSeen.Count > 0)
            Updated?.Invoke(_lastSeen.Values.Select(v => v.Device).ToList());

        while (!_stopped)
        {
            bool force = _forceAll;
            _forceAll = false;
            var polled = new HashSet<IBatteryProvider>();
            var fresh = new HashSet<string>();

            foreach (var provider in _providers)
            {
                var now = DateTime.UtcNow;
                if (!force && _lastPoll.TryGetValue(provider, out var last) && now - last < provider.PollInterval - TimeSpan.FromMilliseconds(500))
                    continue;
                _lastPoll[provider] = now;
                polled.Add(provider);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (var d in provider.Poll())
                    {
                        // Одно и то же устройство может прийти дважды (по проводу и через ресивер) — оставляем первое.
                        if (!fresh.Add(d.Id)) continue;
                        _lastSeen[d.Id] = (d, DateTime.UtcNow);
                        _owner[d.Id] = provider;
                        _misses[d.Id] = 0;
                    }
                    // Одиночный ответ «недоступно» бывает и от помех в эфире — убираем после двух подряд.
                    foreach (var id in provider.Offline)
                    {
                        if (fresh.Contains(id) || !_lastSeen.ContainsKey(id)) continue;
                        fresh.Add(id); // чтобы ниже не посчитать пропуск второй раз
                        if ((_misses[id] = _misses.GetValueOrDefault(id) + 1) < MissesToHide) continue;
                        Log.Write($"{_lastSeen[id].Device.Name}: switched off");
                        Forget(id);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(provider.Name, ex);
                }
                if (timer.ElapsedMilliseconds > 2000)
                    Log.Write($"{provider.Name}: slow poll {timer.ElapsedMilliseconds} ms");
            }

            var nowUtc = DateTime.UtcNow;
            foreach (var id in _lastSeen.Keys.ToList())
            {
                if (fresh.Contains(id)) continue;
                if (IsSticky(id))
                {
                    // Спящая мышь не отвечает ресиверу — показываем последнее значение, пока она не молчит слишком долго.
                    if (nowUtc - _lastSeen[id].Seen > StickyStaleAfter) Forget(id);
                }
                else if (_owner.TryGetValue(id, out var owner) && polled.Contains(owner)
                         && (_misses[id] = _misses.GetValueOrDefault(id) + 1) >= MissesToHide)
                {
                    Forget(id);
                }
            }
            var shown = Deduplicate(_lastSeen.Values.Select(v => v.Device).ToList());

            string summary = string.Join(", ", shown.Select(d => $"{d.Name}={d.Percent?.ToString() ?? "?"}%/{d.State}"));
            if (summary != _lastSummary)
            {
                Log.Write($"shown: {(summary.Length > 0 ? summary : "none")}");
                _lastSummary = summary;
            }
            SaveState();
            Updated?.Invoke(shown);

            _wake.WaitOne(Tick);
        }
    }

    /// <summary>
    /// Устройство из общего модуля (Bluetooth через Windows) прячем, если его уже показывает специализированный:
    /// «DualSense» и «DualSense Wireless Controller» — один и тот же геймпад.
    /// </summary>
    private List<DeviceBattery> Deduplicate(List<DeviceBattery> devices)
    {
        bool IsFallback(DeviceBattery d) => _owner.TryGetValue(d.Id, out var p) && p.IsFallback;
        // Номер у однотипных устройств («DualSense 2») при сравнении не учитываем.
        var specific = devices.Where(d => !IsFallback(d))
            .Select(d => System.Text.RegularExpressions.Regex.Replace(d.Name, @"\s+\d+$", ""))
            .ToList();
        return devices.Where(d => !IsFallback(d) || !specific.Any(name =>
                d.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(d.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void Forget(string id)
    {
        _lastSeen.Remove(id);
        _owner.Remove(id);
        _misses.Remove(id);
    }

    private sealed record SavedEntry(DeviceBattery Device, DateTime Seen);

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var entries = JsonSerializer.Deserialize<List<SavedEntry>>(File.ReadAllText(StatePath));
            var now = DateTime.UtcNow;
            foreach (var e in entries ?? new List<SavedEntry>())
            {
                // Старые записи Logitech без «|» в Id — от прежней версии, где Id строился по имени.
                bool legacy = e.Device.Id.StartsWith("logi:", StringComparison.Ordinal) && !e.Device.Id.Contains('|');
                if (IsSticky(e.Device.Id) && !legacy && now - e.Seen <= StickyStaleAfter)
                    _lastSeen[e.Device.Id] = (e.Device, e.Seen);
            }
            Log.Write($"restored {_lastSeen.Count} device(s) from last run");
        }
        catch (Exception ex)
        {
            Log.Error("load state", ex);
        }
    }

    private void SaveState()
    {
        try
        {
            var entries = _lastSeen.Where(kv => IsSticky(kv.Key))
                .Select(kv => new SavedEntry(kv.Value.Device, kv.Value.Seen)).ToList();
            string json = JsonSerializer.Serialize(entries);
            if (json == _savedState) return;
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllText(StatePath, json);
            _savedState = json;
        }
        catch (Exception ex)
        {
            Log.Error("save state", ex);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
    }
}
