using System;
using System.IO;

namespace BatteryBar;

/// <summary>Простой журнал в %LOCALAPPDATA%\BatteryBar\log.txt — для диагностики протоколов.</summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Sync = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryBar");

    public static string FilePath { get; } = Path.Combine(Directory, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Журнал не должен ронять приложение.
        }
    }

    public static void Error(string context, Exception ex) => Write($"[{context}] {ex.GetType().Name}: {ex.Message}");
}
