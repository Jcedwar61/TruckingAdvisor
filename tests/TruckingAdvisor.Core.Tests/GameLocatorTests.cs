using TruckingAdvisor.Telemetry;
using Xunit;

namespace TruckingAdvisor.Core.Tests;

/// <summary>Drive search runs against a fake Windows file system.</summary>
public sealed class GameLocatorTests
{
    private sealed class FakeDisk
    {
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Contents = new(StringComparer.OrdinalIgnoreCase);

        public void AddGame(string folder, string exe = "amtrucks.exe") => Files.Add($@"{folder}\bin\win_x64\{exe}");

        public bool Dir(string path) =>
            Files.Concat(Contents.Keys).Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static GameLocator Locator(FakeDisk disk, string[]? drives = null,
        (string, string)[]? registry = null, (string?, string?, string)[]? uninstall = null) =>
        new(disk.Dir, disk.Files.Contains, p => disk.Contents.TryGetValue(p, out var c) ? c : null,
            () => drives ?? new[] { @"C:\" }, () => registry ?? Array.Empty<(string, string)>(),
            () => uninstall ?? Array.Empty<(string?, string?, string)>());

    [Fact]
    public void FindsSteamLibraryOnAnotherDrive_WithoutRegistry()
    {
        var disk = new FakeDisk();
        disk.AddGame(@"F:\SteamLibrary\steamapps\common\American Truck Simulator");
        var found = Locator(disk, drives: new[] { @"C:\", @"D:\", @"F:\" }).FindAll();
        Assert.Equal(@"F:\SteamLibrary\steamapps\common\American Truck Simulator", Assert.Single(found).InstallFolder);
        Assert.Equal(TruckGame.AmericanTruckSimulator, found[0].Game);
    }

    [Fact]
    public void FollowsSteamLibraryList_ToAnyFolder()
    {
        var disk = new FakeDisk();
        disk.Contents[@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"] =
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"G:\\\\My Games\\\\Valve\"\n\t}\n}";
        disk.AddGame(@"G:\My Games\Valve\steamapps\common\American Truck Simulator");
        disk.AddGame(@"G:\My Games\Valve\steamapps\common\Euro Truck Simulator 2", "eurotrucks2.exe");
        var found = Locator(disk, registry: new[] { (@"c:/program files (x86)/steam", "Steam registry") }).FindAll();
        Assert.Equal(2, found.Count);
        Assert.Contains(found, g => g.Game == TruckGame.EuroTruckSimulator2);
    }

    [Fact]
    public void UsesWindowsInstalledProgramsEntry()
    {
        var disk = new FakeDisk();
        disk.AddGame(@"H:\Odd Place\ATS");
        var found = Locator(disk, uninstall: new (string?, string?, string)[]
        {
            ("Some Other App", @"C:\Other", "Other"),
            ("American Truck Simulator", @"H:\Odd Place\ATS", "Steam App 270880")
        }).FindAll();
        Assert.Equal(@"H:\Odd Place\ATS", Assert.Single(found).InstallFolder);
    }

    [Fact]
    public void VerifiesUserPickedFolder_EvenIfTheyPickBinFolder_AndRejectsOthers()
    {
        var disk = new FakeDisk();
        disk.AddGame(@"E:\ATS");
        var locator = Locator(disk);
        Assert.Equal(@"E:\ATS", locator.Verify(@"E:\ATS\bin\win_x64")!.InstallFolder);
        Assert.Equal(@"E:\ATS", locator.Verify(@"E:\ATS")!.InstallFolder);
        Assert.Null(locator.Verify(@"E:\Movies"));
    }

    [Fact]
    public void NoDuplicates_WhenFoundSeveralWays()
    {
        var disk = new FakeDisk();
        disk.AddGame(@"D:\SteamLibrary\steamapps\common\American Truck Simulator");
        var found = Locator(disk, drives: new[] { @"D:\" },
            uninstall: new (string?, string?, string)[] { ("American Truck Simulator", @"D:\SteamLibrary\steamapps\common\American Truck Simulator", "Steam App 270880") })
            .FindAll(new[] { @"D:\SteamLibrary\steamapps\common\American Truck Simulator\" });
        Assert.Single(found);
    }

    [Fact]
    public void ParsesOldAndNewLibraryFormats()
    {
        Assert.Equal(new[] { @"D:\Games\Steam" }, GameLocator.ParseLibraryFolders("\"1\"\t\t\"D:\\\\Games\\\\Steam\""));
        Assert.Equal(new[] { @"E:\SteamLibrary" }, GameLocator.ParseLibraryFolders("\"path\"\t\t\"E:\\\\SteamLibrary\""));
    }
}
