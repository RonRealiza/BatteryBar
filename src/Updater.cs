using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BatteryBar;

internal enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>Последний релиз на GitHub и его файлы (с контрольными суммами SHA-256, если GitHub их отдал).</summary>
internal sealed record UpdateInfo(Version Version, string PageUrl, string? SetupUrl, string? SetupSha256, string? PortableUrl, string? PortableSha256);

/// <summary>
/// Проверка и установка обновлений с GitHub Releases. Один запрос к открытому API GitHub, без аккаунта и без
/// отправки данных о пользователе. Обычная версия обновляется установщиком в тихом режиме, портативная —
/// заменой своего exe. Скачанный файл сверяется с контрольной суммой, которую публикует GitHub.
/// </summary>
internal sealed class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/RonRealiza/BatteryBar/releases/latest";

    /// <summary>Версия запущенной программы (без номера сборки). Объявлена до Http: её использует User-Agent.</summary>
    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();
    private readonly SynchronizationContext _ui;

    public Updater(SynchronizationContext ui) => _ui = ui;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Available { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Состояние изменилось. Вызывается в UI-потоке.</summary>
    public event Action? Changed;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    public async Task CheckAsync()
    {
        if (IsBusy) return;
        Set(UpdateState.Checking);
        try
        {
            var info = await Task.Run(FetchLatestAsync);
            Available = info.Version > Current ? info : null;
            Log.Write($"update: current {Current}, latest {info.Version}");
            Set(Available != null ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (Exception ex)
        {
            Log.Error("update check", ex);
            Error = ex is HttpRequestException or TaskCanceledException ? L.UpdateNoConnection : ex.Message;
            Set(UpdateState.Failed);
        }
    }

    /// <param name="exitApp">Закрыть программу — установщику и замене exe нужны свободные файлы.</param>
    public async Task InstallAsync(Action exitApp)
    {
        if (IsBusy || Available is not { } info) return;
        bool portable = Log.IsPortable;
        string? url = portable ? info.PortableUrl : info.SetupUrl;
        string? sha = portable ? info.PortableSha256 : info.SetupSha256;
        if (url == null)
        {
            Error = L.UpdateNoFile;
            Set(UpdateState.Failed);
            return;
        }

        try
        {
            Progress = 0;
            Set(UpdateState.Downloading);
            string dir = Path.Combine(Path.GetTempPath(), "BatteryBar-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));

            await Task.Run(() => DownloadAsync(url, file));
            if (sha != null && !string.Equals(await Task.Run(() => Sha256(file)), sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.UpdateBadChecksum);

            Set(UpdateState.Installing);
            Log.Write($"update: installing {info.Version} ({(portable ? "portable" : "setup")})");
            if (portable)
                ReplacePortableExe(file, dir);
            else
                // Установщик закроет нас сам, поставит новую версию в ту же папку и запустит её (см. installer\BatteryBar.iss).
                Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            exitApp();
        }
        catch (Exception ex)
        {
            Log.Error("update install", ex);
            Error = ex is HttpRequestException or TaskCanceledException ? L.UpdateNoConnection : ex.Message;
            Set(UpdateState.Failed);
        }
    }

    /// <summary>
    /// Портативная версия: запущенный exe нельзя перезаписать, но можно переименовать. Старый становится
    /// BatteryBar.exe.old (его удалит новая версия при запуске), на его место кладём новый и запускаем.
    /// </summary>
    private static void ReplacePortableExe(string zip, string dir)
    {
        string extracted = Path.Combine(dir, "unpacked");
        ZipFile.ExtractToDirectory(zip, extracted);
        string newExe = Directory.GetFiles(extracted, "BatteryBar.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new FileNotFoundException("BatteryBar.exe");
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("process path");
        string old = current + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(current, old);
        try
        {
            File.Copy(newExe, current);
        }
        catch
        {
            File.Move(old, current); // вернуть как было
            throw;
        }
        Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = false });
    }

    private async Task<UpdateInfo> FetchLatestAsync()
    {
        using var response = await Http.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = Normalize(Version.Parse(tag.TrimStart('v', 'V')));
        string page = root.GetProperty("html_url").GetString() ?? DeviceReport.RepoUrl;

        string? setupUrl = null, setupSha = null, portableUrl = null, portableSha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string? link = asset.GetProperty("browser_download_url").GetString();
            // GitHub отдаёт «digest»: "sha256:…" — по нему сверяем скачанный файл.
            string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) (setupUrl, setupSha) = (link, sha);
            else if (name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase)) (portableUrl, portableSha) = (link, sha);
        }
        return new UpdateInfo(version, page, setupUrl, setupSha, portableUrl, portableSha);
    }

    private async Task DownloadAsync(string url, string file)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(file);
        var buffer = new byte[81920];
        long done = 0;
        int read, lastReported = -1;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            int percent = total > 0 ? (int)(done * 100 / total) : 0;
            if (percent != lastReported)
            {
                lastReported = percent;
                _ui.Post(_ =>
                {
                    Progress = percent;
                    Changed?.Invoke();
                }, null);
            }
        }
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void Set(UpdateState state)
    {
        if (state != UpdateState.Failed) Error = null;
        _ui.Post(_ =>
        {
            State = state;
            Changed?.Invoke();
        }, null);
        State = state;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // GitHub API требует User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BatteryBar/{Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
