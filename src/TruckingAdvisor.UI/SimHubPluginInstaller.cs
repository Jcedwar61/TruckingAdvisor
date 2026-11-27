using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TruckingAdvisor.UI;

/// <summary>
/// Finds SimHub and copies TruckingAdvisor's SimHub plugin (carried inside this exe) into its folder, with a small
/// config file holding the port TruckingAdvisor's panel uses. SimHub loads plugins from its own folder at start, so
/// SimHub has to be restarted once (it then asks whether to enable the new plugin).
/// </summary>
public static class SimHubPluginInstaller
{
    public const string PluginFile = "TruckingAdvisor.SimHubPlugin.dll";
    public const string ConfigFile = "TruckingAdvisor.SimHubPlugin.cfg";
    private const string SimHubExe = "SimHubWPF.exe";

    /// <summary>SimHub's folder: from the running SimHub, the installed-programs list, or the default folders. Null if not found.</summary>
    public static string? FindSimHub()
    {
        foreach (var dir in Candidates())
        {
            try { if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, SimHubExe))) return dir; }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        foreach (var p in Process.GetProcessesByName("SimHubWPF"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            finally { p.Dispose(); }
            if (path is not null) yield return Path.GetDirectoryName(path);
        }
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var view in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = root.OpenSubKey(view);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app?.GetValue("DisplayName") is not string display || !display.StartsWith("SimHub", StringComparison.OrdinalIgnoreCase)) continue;
                    if (app.GetValue("InstallLocation") is string location) yield return location.Trim('"');
                    if (app.GetValue("DisplayIcon") is string icon) yield return Path.GetDirectoryName(icon.Split(',')[0].Trim('"'));
                }
            }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SimHub");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SimHub");
    }

    public static bool IsSimHubRunning()
    {
        var running = Process.GetProcessesByName("SimHubWPF");
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    /// <summary>The plugin as carried in this build.</summary>
    public static byte[] PluginBytes()
    {
        using var s = typeof(SimHubPluginInstaller).Assembly.GetManifestResourceStream(PluginFile)
            ?? throw new InvalidOperationException("The SimHub plugin is missing from this build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>True when SimHub's folder already holds this exact plugin and config.</summary>
    public static bool IsInstalled(string simHubDir, int port)
    {
        try
        {
            string plugin = Path.Combine(simHubDir, PluginFile), config = Path.Combine(simHubDir, ConfigFile);
            return File.Exists(plugin) && File.Exists(config)
                && File.ReadAllBytes(plugin).AsSpan().SequenceEqual(PluginBytes())
                && File.ReadAllText(config) == Config(port);
        }
        catch { return false; }
    }

    public static string Config(int port) => $"# Written by TruckingAdvisor: the port of its SimHub panel.\nport={port}\n";

    /// <summary>
    /// Copies the plugin and its config into SimHub's folder. SimHub usually sits in Program Files, so when a plain
    /// copy is refused it asks Windows for permission (one UAC prompt) and copies again.
    /// </summary>
    /// <returns>Null when done, else why not (in words for the player).</returns>
    public static async Task<string?> InstallAsync(string simHubDir, int port)
    {
        string staging = Path.Combine(Path.GetTempPath(), "TruckingAdvisor SimHub plugin");
        Directory.CreateDirectory(staging);
        string stagedPlugin = Path.Combine(staging, PluginFile), stagedConfig = Path.Combine(staging, ConfigFile);
        await File.WriteAllBytesAsync(stagedPlugin, PluginBytes());
        await File.WriteAllTextAsync(stagedConfig, Config(port));
        try
        {
            File.Copy(stagedPlugin, Path.Combine(simHubDir, PluginFile), overwrite: true);
            File.Copy(stagedConfig, Path.Combine(simHubDir, ConfigFile), overwrite: true);
            return null;
        }
        catch (IOException) when (IsSimHubRunning())
        {
            return "SimHub is using the plugin. Close SimHub, then try again.";
        }
        catch (UnauthorizedAccessException)
        {
            // Program Files: copy with administrator rights.
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c copy /y \"{stagedPlugin}\" \"{simHubDir}\\\" && copy /y \"{stagedConfig}\" \"{simHubDir}\\\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using var p = Process.Start(psi);
                if (p is null) return "Windows didn't start the copy.";
                await p.WaitForExitAsync();
                return p.ExitCode == 0 && IsInstalled(simHubDir, port) ? null
                    : IsSimHubRunning() ? "SimHub is using the plugin. Close SimHub, then try again." : $"The copy failed (code {p.ExitCode}).";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Administrator permission was refused, so the plugin couldn't be copied into SimHub's folder.";
            }
        }
    }
}
