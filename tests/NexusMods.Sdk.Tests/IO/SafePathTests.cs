using NexusMods.Paths;
using NexusMods.Sdk.IO;

namespace NexusMods.Sdk.Tests.IO;

public class SafePathTests
{
    [Test]
    [Arguments("/game", "/game/r6/mod.reds", true)]
    [Arguments("/game/", "/game/r6/mod.reds", true)]
    [Arguments("/game", "/game/./r6", true)]
    [Arguments("/game", "/game", false)]
    [Arguments("/game", "/game/.", false)]
    [Arguments("/game", "/game/../home/.bashrc", false)]
    [Arguments("/game", "/game/r6/../../x", false)]
    [Arguments("/game", "/gamefoo/x", false)]
    [Arguments("/game", "/etc/passwd", false)]
    public async Task IsStrictlyInside(string root, string path, bool expected)
    {
        await Assert.That(SafePath.IsStrictlyInside(root, path)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("r6/scripts/mod.reds", false)]
    [Arguments("..foo/bar", false)]
    [Arguments("../x", true)]
    [Arguments("r6/../../x", true)]
    public async Task HasParentSegment(string path, bool expected)
    {
        await Assert.That(SafePath.HasParentSegment(RelativePath.FromUnsanitizedInput(path))).IsEqualTo(expected);
    }

    [Test]
    public async Task IsUnderSymlink_DetectsLinkedParentOnly()
    {
        var root = Directory.CreateTempSubdirectory("safepath-").FullName;
        var outside = Directory.CreateTempSubdirectory("safepath-outside-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "real"));
            File.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

            await Assert.That(SafePath.IsUnderSymlink(root, Path.Combine(root, "real/file.txt"))).IsFalse();
            await Assert.That(SafePath.IsUnderSymlink(root, Path.Combine(root, "linked/file.txt"))).IsTrue();
            // The path itself being a link is fine: deleting or moving it only touches the link
            await Assert.That(SafePath.IsUnderSymlink(root, Path.Combine(root, "linked"))).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
