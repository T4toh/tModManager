using NexusMods.Paths;

namespace NexusMods.Sdk.Tests;

public class WinePathsTests
{
    private static readonly AbsolutePath Prefix = FileSystem.Shared.FromUnsanitizedFullPath("/home/u/.local/share/Steam/steamapps/compatdata/1091500/pfx");

    [Test]
    public async Task FileInDriveCBecomesC()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("drive_c/modlist.txt"), Prefix)).IsEqualTo(@"C:\modlist.txt");

    [Test]
    public async Task NestedFileInDriveCKeepsItsFolders()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("drive_c/users/steamuser/AppData/Local/x.json"), Prefix)).IsEqualTo(@"C:\users\steamuser\AppData\Local\x.json");

    [Test]
    public async Task FileOutsideThePrefixBecomesZ()
        => await Assert.That(WinePaths.ToWindowsPath(FileSystem.Shared.FromUnsanitizedFullPath("/home/u/Juegos/Cyberpunk 2077/tools/redmod/bin/redMod.exe"), Prefix))
            .IsEqualTo(@"Z:\home\u\Juegos\Cyberpunk 2077\tools\redmod\bin\redMod.exe");

    [Test]
    public async Task FileInThePrefixButOutsideDriveCBecomesZ()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("user.reg"), Prefix)).StartsWith(@"Z:\");
}
