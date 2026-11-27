using System.Security.Cryptography;
using System.Text;

namespace TruckingAdvisor.AI;

/// <summary>
/// Stores the API key encrypted with Windows DPAPI (current user scope) under
/// %LOCALAPPDATA%\TruckingAdvisor. There is no plaintext fallback: on other platforms
/// saving fails and loading returns null.
///
/// The decrypted key is cached in memory (it's asked for several times a second by availability checks), and the
/// cache is dropped on Save/Delete or when the file changes on disk. Files are written atomically.
/// </summary>
public static class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TruckingAdvisor.ApiKey.v1");

    private sealed record Cached(DateTime WriteTimeUtc, long Length, string? Key);
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");

    private static string KeyPath(AiLlmProvider provider, string? dir) =>
        Path.GetFullPath(Path.Combine(dir ?? DefaultDirectory, $"{provider.ToString().ToLowerInvariant()}_key.dpapi"));

    public static void Save(string plainKey, AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("API keys can only be stored with Windows DPAPI.");
        var path = KeyPath(provider, dir);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Invalidate(path);
        // Temp file + swap: a crash mid-write can't leave a truncated (silently unreadable) key behind.
        AtomicFile.Write(path, encrypted);
        Invalidate(path);
    }

    public static string? Load(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = KeyPath(provider, dir);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { Invalidate(path); return null; }
            lock (CacheLock)
                if (Cache.TryGetValue(path, out var c) && c.WriteTimeUtc == info.LastWriteTimeUtc && c.Length == info.Length)
                    return c.Key;

            string? key;
            try
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                key = Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                key = null; // saved by another Windows user or machine
            }
            lock (CacheLock) Cache[path] = new Cached(info.LastWriteTimeUtc, info.Length, key);
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // locked or unreadable right now: no key (retried next time, not cached)
        }
    }

    public static bool Exists(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null) =>
        File.Exists(KeyPath(provider, dir));

    public static void Delete(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        var path = KeyPath(provider, dir);
        Invalidate(path);
        if (File.Exists(path)) File.Delete(path);
        Invalidate(path);
    }

    private static void Invalidate(string path)
    {
        lock (CacheLock) Cache.Remove(path);
    }
}
