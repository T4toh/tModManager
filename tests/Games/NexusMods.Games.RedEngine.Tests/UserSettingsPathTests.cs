using FluentAssertions;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Paths;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>Proton prefixes use <c>steamuser</c>; a Wine/Lutris prefix uses the real user name.</summary>
public class UserSettingsPathTests
{
    private const string Suffix = "/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json";

    private static AbsolutePath TempPrefix() =>
        FileSystem.Shared.FromUnsanitizedFullPath(Directory.CreateTempSubdirectory("tmm-prefix-").FullName);

    [Fact]
    public void NoUserFolderYet_AssumesSteamuser()
    {
        var prefix = TempPrefix();
        Cyberpunk2077Game.UserSettingsPath(prefix).ToString().Should().Be("drive_c/users/steamuser" + Suffix);
    }

    [Fact]
    public void LutrisPrefix_UsesTheRealUser()
    {
        var prefix = TempPrefix();
        prefix.Combine("drive_c/users").Combine(Environment.UserName).CreateDirectory();

        Cyberpunk2077Game.UserSettingsPath(prefix).ToString().Should().Be($"drive_c/users/{Environment.UserName}" + Suffix);
    }

    [Fact]
    public void BothFolders_SteamuserWins()
    {
        var prefix = TempPrefix();
        prefix.Combine("drive_c/users").Combine(Environment.UserName).CreateDirectory();
        prefix.Combine("drive_c/users/steamuser").CreateDirectory();

        Cyberpunk2077Game.UserSettingsPath(prefix).ToString().Should().Be("drive_c/users/steamuser" + Suffix);
    }
}
