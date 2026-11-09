using System.Text.RegularExpressions;

namespace TruckingAdvisor.Telemetry;

public enum TruckGame { AmericanTruckSimulator, EuroTruckSimulator2 }

/// <summary>A game install that was verified on disk (its 64-bit executable exists).</summary>
public sealed record GameInstall(TruckGame Game, string InstallFolder, string Source)
{
    public string Name => Game == TruckGame.AmericanTruckSimulator ? "American Truck Simulator" : "Euro Truck Simulator 2";
    public string BinFolder => Path.Combine(InstallFolder, "bin", "win_x64");
    public string PluginFolder => Path.Combine(BinFolder, "plugins");
}

/// <summary>
/// Finds ATS / ETS2 wherever they are installed:
/// 1. Steam's install path from the registry (current user and machine-wide), and every library in its libraryfolders.vdf;
/// 2. Windows "installed programs" entries (Steam app 270880 / 227300, or any entry named after the game);
/// 3. Common folders on every ready fixed drive (C: through Z:), e.g. D:\SteamLibrary, E:\Games\Steam;
/// 4. Folders the user picked by hand.
/// A folder only counts if bin\win_x64\amtrucks.exe (or eurotrucks2.exe) exists.
/// File system and registry access are injectable so the search logic can be tested.
/// </summary>
public sealed class GameLocator
{
    public const string AtsSteamAppId = "270880";
    public const string Ets2SteamAppId = "227300";

    private static readonly (TruckGame Game, string Folder, string Exe, string AppId)[] Games =
    {
        (TruckGame.AmericanTruckSimulator, "American Truck Simulator", "amtrucks.exe", AtsSteamAppId),
        (TruckGame.EuroTruckSimulator2, "Euro Truck Simulator 2", "eurotrucks2.exe", Ets2SteamAppId)
    };

    // Relative to a drive root.
    private static readonly string[] SteamRootsPerDrive =
    {
        "Steam", "SteamLibrary", "Steam Library", "Program Files (x86)\\Steam", "Program Files\\Steam",
        "Games\\Steam", "Games\\SteamLibrary", "Games\\Steam Library", "SteamGames"
    };

    private static readonly string[] DirectGameFoldersPerDrive = { "", "Games", "Program Files", "Program Files (x86)" };

    private readonly Func<string, bool> _dirExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _readFile;
    private readonly Func<IEnumerable<string>> _driveRoots;
    private readonly Func<IEnumerable<(string Value, string Source)>> _registrySteamPaths;
    private readonly Func<IEnumerable<(string? DisplayName, string? InstallLocation, string KeyName)>> _uninstallEntries;

    public GameLocator(
        Func<string, bool>? dirExists = null,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? readFile = null,
        Func<IEnumerable<string>>? driveRoots = null,
        Func<IEnumerable<(string Value, string Source)>>? registrySteamPaths = null,
        Func<IEnumerable<(string? DisplayName, string? InstallLocation, string KeyName)>>? uninstallEntries = null)
    {
        _dirExists = dirExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
        _readFile = readFile ?? (p => { try { return File.ReadAllText(p); } catch { return null; } });
        _driveRoots = driveRoots ?? FixedDriveRoots;
        _registrySteamPaths = registrySteamPaths ?? RegistrySteamPaths;
        _uninstallEntries = uninstallEntries ?? UninstallEntries;
    }

    /// <summary>Every verified install, without duplicates. <paramref name="extraFolders"/> are folders the user picked.</summary>
    public IReadOnlyList<GameInstall> FindAll(IEnumerable<string>? extraFolders = null)
    {
        var found = new List<GameInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryGameFolder(string folder, string source)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            folder = Normalize(folder);
            // Accept the game folder itself, or its bin / bin\win_x64 subfolder.
            foreach (var candidate in new[] { folder, Parent(folder), Parent(Parent(folder)) })
            {
                if (candidate is null) continue;
                foreach (var g in Games)
                {
                    if (_fileExists(Join(candidate, "bin", "win_x64", g.Exe)) && seen.Add(candidate))
                    {
                        found.Add(new GameInstall(g.Game, candidate, source));
                        return;
                    }
                }
            }
        }

        void TrySteamLibrary(string library, string source)
        {
            foreach (var g in Games)
                TryGameFolder(Join(library, "steamapps", "common", g.Folder), source);
        }

        // 1. Steam from the registry, plus all of its libraries.
        var steamRoots = new List<(string Root, string Source)>();
        foreach (var (value, source) in _registrySteamPaths()) steamRoots.Add((Normalize(value), source));

        // 3. Common Steam folders on every drive.
        var drives = _driveRoots().ToList();
        foreach (var drive in drives)
            foreach (var rel in SteamRootsPerDrive)
                steamRoots.Add((Join(drive, rel), $"drive scan ({drive})"));

        var libraries = new List<(string Lib, string Source)>();
        foreach (var (root, source) in steamRoots)
        {
            if (!_dirExists(root)) continue;
            libraries.Add((root, source));
            foreach (var lib in ParseLibraryFolders(_readFile(Join(root, "steamapps", "libraryfolders.vdf"))))
                libraries.Add((Normalize(lib), "Steam library list"));
        }
        foreach (var (lib, source) in libraries) TrySteamLibrary(lib, source);

        // 2. Windows installed-programs entries.
        foreach (var (name, location, key) in _uninstallEntries())
        {
            bool isGame = Games.Any(g => key.EndsWith("App " + g.AppId, StringComparison.OrdinalIgnoreCase) ||
                                         (name?.Contains(g.Folder, StringComparison.OrdinalIgnoreCase) ?? false));
            if (isGame && !string.IsNullOrWhiteSpace(location)) TryGameFolder(location!, "Windows installed programs");
        }

        // Non-Steam copies in obvious places.
        foreach (var drive in drives)
            foreach (var rel in DirectGameFoldersPerDrive)
                foreach (var g in Games)
                    TryGameFolder(Join(drive, rel, g.Folder), $"drive scan ({drive})");

        // 4. Folders the user picked.
        foreach (var extra in extraFolders ?? Array.Empty<string>()) TryGameFolder(extra, "chosen by you");

        return found;
    }

    /// <summary>True if the folder (or its bin / bin\win_x64 parent chain) is an ATS or ETS2 install.</summary>
    public GameInstall? Verify(string folder) => FindAllFrom(folder);

    private GameInstall? FindAllFrom(string folder)
    {
        var locator = new GameLocator(_dirExists, _fileExists, _readFile, () => Array.Empty<string>(),
            () => Array.Empty<(string, string)>(), () => Array.Empty<(string?, string?, string)>());
        return locator.FindAll(new[] { folder }).FirstOrDefault();
    }

    /// <summary>Library paths from Steam's libraryfolders.vdf (both the old and new formats).</summary>
    public static IEnumerable<string> ParseLibraryFolders(string? vdf)
    {
        if (string.IsNullOrEmpty(vdf)) yield break;
        // New format: "path" "D:\\SteamLibrary". Old format: "1" "D:\\SteamLibrary".
        foreach (Match m in Regex.Matches(vdf, "\"(?:path|\\d+)\"\\s+\"([A-Za-z]:[^\"]*)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    /// <summary>Windows-style join (the app only runs on Windows; keeps the search testable elsewhere).</summary>
    private static string Join(params string[] parts) =>
        string.Join('\\', parts.Where(p => !string.IsNullOrEmpty(p)).Select((p, i) => i == 0 ? p.TrimEnd('\\') : p.Trim('\\')));

    private static string Normalize(string p) => p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    private static string? Parent(string? p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        int i = p.TrimEnd('\\').LastIndexOf('\\');
        return i > 0 ? p[..i] : null;
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<(string, string)> RegistrySteamPaths()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var probes = new (Microsoft.Win32.RegistryKey Hive, string Key, string Value)[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
        };
        foreach (var (hive, key, value) in probes)
        {
            string? path = null;
            try
            {
                using var k = hive.OpenSubKey(key);
                path = k?.GetValue(value) as string;
            }
            catch { /* no access: skip */ }
            if (!string.IsNullOrWhiteSpace(path)) yield return (path!, "Steam registry");
        }
    }

    private static IEnumerable<(string?, string?, string)> UninstallEntries()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var roots = new (Microsoft.Win32.RegistryKey Hive, string Key)[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        foreach (var (hive, key) in roots)
        {
            var entries = new List<(string?, string?, string)>();
            try
            {
                using var uninstall = hive.OpenSubKey(key);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    entries.Add((sub?.GetValue("DisplayName") as string, sub?.GetValue("InstallLocation") as string, name));
                }
            }
            catch { /* no access: skip */ }
            foreach (var e in entries) yield return e;
        }
    }
}
