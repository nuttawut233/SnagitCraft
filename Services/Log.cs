using System;
using System.IO;

namespace SnagitAnything.Services;

/// <summary>Tiny append-only log in %APPDATA%\SnagitAnything\log.txt, so failures are recorded instead of killing the app.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppSettings.ConfigFolder, "log.txt");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.ConfigFolder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the thing that crashes the app.
        }
    }
}
