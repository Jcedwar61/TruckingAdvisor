using System.Security.Cryptography;
using System.Text;

namespace TruckingAdvisor.AI;

/// <summary>
/// Stores the API key encrypted with Windows DPAPI (current user scope) under
/// %LOCALAPPDATA%\TruckingAdvisor. There is no plaintext fallback: on other platforms
/// saving fails and loading returns null.
/// </summary>
public static class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TruckingAdvisor.ApiKey.v1");

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");

    private static string KeyPath(AiLlmProvider provider, string? dir) =>
        Path.Combine(dir ?? DefaultDirectory, $"{provider.ToString().ToLowerInvariant()}_key.dpapi");

    public static void Save(string plainKey, AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("API keys can only be stored with Windows DPAPI.");
        var path = KeyPath(provider, dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encrypted);
    }

    public static string? Load(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = KeyPath(provider, dir);
        if (!File.Exists(path)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // saved by another Windows user or machine
        }
    }

    public static bool Exists(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null) =>
        File.Exists(KeyPath(provider, dir));

    public static void Delete(AiLlmProvider provider = AiLlmProvider.OpenAI, string? dir = null)
    {
        var path = KeyPath(provider, dir);
        if (File.Exists(path)) File.Delete(path);
    }
}
