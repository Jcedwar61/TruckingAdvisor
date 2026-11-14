using System.Text;

namespace TruckingAdvisor.Shared.Diagnostics;

/// <summary>
/// Small rolling log file for troubleshooting (%LOCALAPPDATA%\TruckingAdvisor\logs\app.log).
/// Never throws. Every line is scrubbed: the Windows user name and profile path are replaced,
/// and anything that looks like an API key is removed, so a log can be shared safely.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static string? _path;
    private static readonly LinkedList<string> Recent = new();

    public static string? FilePath => _path;

    /// <summary>Call once at startup. Without it, lines are only kept in memory.</summary>
    public static void Init(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "app.log");
        }
        catch { _path = null; }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>The last lines written this session (newest last).</summary>
    public static IReadOnlyList<string> Tail(int lines = 60)
    {
        lock (Gate) return Recent.Skip(Math.Max(0, Recent.Count - lines)).ToList();
    }

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {Scrub(message)}";
            lock (Gate)
            {
                Recent.AddLast(line);
                while (Recent.Count > 300) Recent.RemoveFirst();
                if (_path is null) return;
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = _path + ".old";
                    File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* logging must never break the app */ }
    }

    /// <summary>Removes personal details: user profile path, user name, API keys / bearer tokens.</summary>
    public static string Scrub(string text) => Scrub(text,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    public static string Scrub(string text, string? userProfile, string? userName)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(userProfile) && userProfile.Length > 3)
            text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
            text = System.Text.RegularExpressions.Regex.Replace(text,
                $@"(?<=[\\/]Users[\\/]){System.Text.RegularExpressions.Regex.Escape(userName)}(?=[\\/]|$)", "<user>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bsk-[A-Za-z0-9_\-]{8,}", "sk-<removed>");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._\-]{8,}", "Bearer <removed>");
        return text;
    }
}
