# Local Mods First-Class (pieza 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A mod file added by hand ends up in `tModManager/Downloads` with `LibraryFile.DownloadPath` (so it re-extracts, survives GC and Deep Clean, and counts in the Storage Manager), with optional name/version/source/URL the user can set when adding and edit later from the library.

**Architecture:** `AddLocalFileJob` copies the chosen file into the downloads folder through the existing `DownloadsFolder.PlaceAsync` (atomic copy, dedupe by content, `_N` suffix), dedupes by xxHash3 against existing `LocalFile`s, then runs the unchanged `AddLibraryFileJob` on the copy so `DownloadPath` is recorded by the normal path. Three new optional attributes on `LocalFile` (`Version`, `Source`, `PageUri`) carry the metadata; `LibraryItem.Name` stays the display name. A hosted service backfills old `LocalFile`s that still have their original on disk. The UI adds a small overlay (same pattern as `ManualAddGameOverlay`) used both when adding and when editing, a toolbar "Editar" button, and a `LocalFileId` case in `ViewModPageMessage` so "Ver página del mod" opens `PageUri`.

**Tech Stack:** C#/.NET 10, MnemonicDB 0.28.2 (`StringAttribute`, `UriAttribute`, `IsOptional`), NexusMods.Paths, `NexusMods.Hashing.xxHash3`, Avalonia + R3 (`BindableReactiveProperty`, `ReactiveCommand`, `AOverlayViewModel`), xUnit v3 (`dotnet test`) for `NexusMods.DataModel.Tests`, TUnit (`dotnet run`) for `NexusMods.Sdk.Tests`, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-10-10-local-mods-first-class-design.md`

## Global Constraints

- Branch `feat/local-mods-first-class` (already created from `main`; the spec is its first commit).
- dotnet lives in `~/.dotnet`; export `DOTNET_ROLL_FORWARD=Major` before any `dotnet` command.
- Build gate: `dotnet build -p:TreatWarningsAsErrors=true` must stay at 0 compiler warnings (only `NU19xx` NuGet audit warnings are tolerated). `.globalconfig` analyzer errors (`CS4014`, `CS8509`, `CA1069`, `CA2211`, `CA2021`) are never suppressed.
- Never run `dotnet test` on the whole solution. One project at a time. TUnit projects (`NexusMods.Sdk.Tests`, `NexusMods.Backend.Tests`) run with `dotnet run --project`, not `dotnet test`.
- Nothing in the core assumes Nexus Mods: `Source` is free text, no field is required, no `NexusModsGameId` anywhere in this work.
- Copy, never move, the user's file. Never create a symlink inside `Downloads`. Never `AbsolutePath.DeleteDirectory(recursive: true)`.
- Every destructive or copying path gets a test with a symlink pointing outside; the test must fail without the fix.
- Commit messages: Conventional Commits, no AI co-author line, no "Generated with" footer (org rule).
- Log, exception and UI strings in Spanish, as the surrounding fork code does. Code comments in English.
- `.verified.` snapshot files are never deleted or edited by hand. If one changes, stop and report.
- No new NuGet packages.

## Review Focus

Inputs the spec implies but did not list as tests. Each has a test pinned to the task that owns the code:

1. The user picks the same file twice from the same outside folder: second call must return the first `LocalFile`, leave exactly one file in `Downloads`, and not throw. Test in Task 2 (`AddLocalFile_SameFileTwice_ReturnsExistingAndLeavesOneCopy`).
2. The downloads folder does not exist yet (fresh install, nothing downloaded): the copy must create it. Test in Task 2 (`AddLocalFile_DownloadsFolderMissing_CreatesIt`).
3. Two adds of the same name race on the same `Downloads/<name>` (two pickers, or the collection rescan running at the same time): must never overwrite or leave a half-written file under a final name. Covered by `DownloadsFolder.PlaceAsync`'s temp+move design and its existing tests in `tests/NexusMods.Library.Tests/DownloadsFolderTests.cs` (`TryClaim_CandidateAppearedMeanwhile_*`); Task 2 only routes through it, no new test.
4. Metadata with surrounding whitespace or an empty string must store nothing (no empty `Version` datom). Test in Task 2 (`AddLocalFile_WhitespaceMetadata_StoresNothing`).
5. Editing metadata on a local file that is installed in a loadout must not touch the loadout group name. Test in Task 4 (`UpdateLocalFileMetadata_DoesNotRenameInstalledGroup`).

---

### Task 1: Model attributes, metadata record and service signature

**Files:**
- Modify: `src/NexusMods.Sdk/Library/Models/LocalFile.cs`
- Create: `src/NexusMods.Abstractions.Library/LocalFileMetadata.cs`
- Modify: `src/NexusMods.Abstractions.Library/Jobs/IAddLocalFile.cs`
- Modify: `src/NexusMods.Abstractions.Library/ILibraryService.cs:26-29`
- Modify: `src/NexusMods.Library/LibraryService.cs:44-47`
- Modify: `src/NexusMods.Library/AddLocalFileJob.cs` (signature only; behaviour in Task 2)

**Interfaces:**
- Produces: `LocalFile.Version` (`StringAttribute`, optional), `LocalFile.Source` (`StringAttribute`, optional), `LocalFile.PageUri` (`UriAttribute`, optional); `record LocalFileMetadata(string? Name = null, string? Version = null, string? Source = null, Uri? PageUri = null)` with `static LocalFileMetadata Empty` and `bool IsEmpty`; `ILibraryService.AddLocalFile(AbsolutePath absolutePath, LocalFileMetadata? metadata = null)`; `IAddLocalFile.Metadata`.

- [ ] **Step 1: Add the attributes**

In `src/NexusMods.Sdk/Library/Models/LocalFile.cs`, after `OriginalPath`:

```csharp
    /// <summary>
    /// Version as the user typed it or as parsed from the file name. Free text, optional.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };

    /// <summary>
    /// Where the file came from ("mod.io", "GitHub", "foro", ...). Free text, optional. Never a Nexus-specific id.
    /// </summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source)) { IsOptional = true };

    /// <summary>
    /// Page of the mod, not the direct download link. Optional.
    /// </summary>
    public static readonly UriAttribute PageUri = new(Namespace, nameof(PageUri)) { IsOptional = true };
```

- [ ] **Step 2: Create the metadata record**

`src/NexusMods.Abstractions.Library/LocalFileMetadata.cs`:

```csharp
using JetBrains.Annotations;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// Optional metadata for a file added by hand. Every field may be null or blank; blank values are never stored.
/// </summary>
[PublicAPI]
public record LocalFileMetadata(string? Name = null, string? Version = null, string? Source = null, Uri? PageUri = null)
{
    public static readonly LocalFileMetadata Empty = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name)
                           && string.IsNullOrWhiteSpace(Version)
                           && string.IsNullOrWhiteSpace(Source)
                           && PageUri is null;
}
```

- [ ] **Step 3: Extend the job interface and the service**

`src/NexusMods.Abstractions.Library/Jobs/IAddLocalFile.cs`: add after `FilePath`:

```csharp
    /// <summary>
    /// Metadata to store with the file. Empty when the caller has none.
    /// </summary>
    public LocalFileMetadata Metadata { get; }
```

`src/NexusMods.Abstractions.Library/ILibraryService.cs`: replace the `AddLocalFile` declaration with

```csharp
    /// <summary>
    /// Adds a local file to the library. A file outside the downloads folder is copied there first, so
    /// it gets a <see cref="LibraryFile.DownloadPath"/> like a download. The user's file is never moved.
    /// </summary>
    IJobTask<IAddLocalFile, LocalFile.ReadOnly> AddLocalFile(AbsolutePath absolutePath, LocalFileMetadata? metadata = null);
```

`src/NexusMods.Library/LibraryService.cs`:

```csharp
    public IJobTask<IAddLocalFile, LocalFile.ReadOnly> AddLocalFile(AbsolutePath absolutePath, LocalFileMetadata? metadata = null)
    {
        return AddLocalFileJob.Create(_serviceProvider, absolutePath, metadata ?? LocalFileMetadata.Empty);
    }
```

`src/NexusMods.Library/AddLocalFileJob.cs`: add `public required LocalFileMetadata Metadata { get; init; }`, change `Create` to `Create(IServiceProvider provider, AbsolutePath filePath, LocalFileMetadata metadata)` and set `Metadata = metadata` in the initializer. Leave `StartAsync` as it is for now.

- [ ] **Step 4: Build**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | tail -3`
Expected: `0 Error(s)`, 0 compiler warnings. Existing callers compile because `metadata` is optional.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Sdk/Library/Models/LocalFile.cs src/NexusMods.Abstractions.Library src/NexusMods.Library
git commit -m "feat(library): optional version, source and page URI on LocalFile"
```

---

### Task 2: `AddLocalFileJob` copies into Downloads, dedupes by hash, stores metadata

**Files:**
- Modify: `src/NexusMods.Library/AddLocalFileJob.cs`
- Create: `tests/NexusMods.DataModel.Tests/AddLocalFileTests.cs`
- Modify: `tests/NexusMods.DataModel.Tests/DownloadReExtractorTests.cs` (one new test)

**Interfaces:**
- Consumes: `LocalFileMetadata`, `LocalFile.Version/Source/PageUri` (Task 1); `DownloadsFolder.PlaceAsync(AbsolutePath source, AbsolutePath folder, string fileName, CancellationToken)` (`NexusMods.Sdk.Library`, exists); `AddLibraryFileJob.Create(IServiceProvider, ITransaction, AbsolutePath)` (exists, unchanged); `LibraryFile.FindByHash(IDb, Hash)` (exists).
- Produces: `internal static void AddLocalFileJob.ApplyMetadata(ITransaction tx, EntityId id, LocalFileMetadata metadata)` (reused by Task 4).

- [ ] **Step 1: Write the failing tests**

`tests/NexusMods.DataModel.Tests/AddLocalFileTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class AddLocalFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<AddLocalFileTests>(helper)
{
    private AbsolutePath Downloads => ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);

    private AbsolutePath Outside(string name, string content = "mod bytes")
    {
        var folder = TemporaryFileManager.CreateFolder().Path;
        var file = folder.Combine(name);
        File.WriteAllText(file.ToString(), content);
        return file;
    }

    private static string[] FilesIn(AbsolutePath folder) =>
        folder.DirectoryExists() ? folder.EnumerateFiles("*", recursive: false).Select(f => f.FileName.ToString()).Order().ToArray() : [];

    [Fact]
    public async Task AddLocalFile_OutsideDownloads_IsCopiedAndRegistered()
    {
        var src = Outside("Mod.zip");

        var local = await LibraryService.AddLocalFile(src);

        src.FileExists.Should().BeTrue("the user's file is never moved");
        FilesIn(Downloads).Should().Equal("Mod.zip");
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Mod.zip");
        local.OriginalPath.Should().Be(src.ToString());
        (await FileStore.HaveFile(local.AsLibraryFile().Hash)).Should().BeTrue();
    }

    [Fact]
    public async Task AddLocalFile_InsideDownloads_IsNotCopied()
    {
        Downloads.CreateDirectory();
        var inside = Downloads.Combine("Inside.zip");
        File.WriteAllText(inside.ToString(), "x");

        var local = await LibraryService.AddLocalFile(inside);

        FilesIn(Downloads).Should().Equal("Inside.zip");
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Inside.zip");
    }

    [Fact]
    public async Task AddLocalFile_DownloadsFolderMissing_CreatesIt()
    {
        Downloads.DirectoryExists().Should().BeFalse();
        await LibraryService.AddLocalFile(Outside("Mod.zip"));
        Downloads.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task AddLocalFile_SameNameDifferentContent_GetsSuffix()
    {
        var first = await LibraryService.AddLocalFile(Outside("Mod.zip", "v1"));
        var second = await LibraryService.AddLocalFile(Outside("Mod.zip", "v2"));

        FilesIn(Downloads).Should().Equal("Mod.zip", "Mod_1.zip");
        LibraryFile.DownloadPath.Get(second.AsLibraryFile()).ToString().Should().Be("Mod_1.zip");
        second.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task AddLocalFile_SameHashTwice_ReturnsExistingAndLeavesNoCopy()
    {
        var first = await LibraryService.AddLocalFile(Outside("A.zip", "same"));
        var second = await LibraryService.AddLocalFile(Outside("B.zip", "same"));

        second.Id.Should().Be(first.Id);
        FilesIn(Downloads).Should().Equal("A.zip");
        LocalFile.All(Connection.Db).Should().ContainSingle();
    }

    [Fact]
    public async Task AddLocalFile_SameFileTwice_ReturnsExistingAndLeavesOneCopy()
    {
        var src = Outside("Mod.zip");
        var first = await LibraryService.AddLocalFile(src);
        var second = await LibraryService.AddLocalFile(src);

        second.Id.Should().Be(first.Id);
        FilesIn(Downloads).Should().Equal("Mod.zip");
    }

    [Fact]
    public async Task AddLocalFile_SameHashAsOldLocalWithoutDownloadPath_RepairsIt()
    {
        // A LocalFile from before this change: registered from outside Downloads, no DownloadPath.
        var old = Outside("Old.zip", "old bytes");
        using (var tx = Connection.BeginTransaction())
        {
            var lib = new LibraryFile.New(tx, out var id)
            {
                FileName = old.FileName,
                Hash = await HashOf(old),
                Size = old.FileInfo.Size,
                LibraryItem = new LibraryItem.New(tx, id) { Name = old.FileName },
            };
            _ = new LocalFile.New(tx, id) { LibraryFile = lib, OriginalPath = old.ToString() };
            await tx.Commit();
        }

        var local = await LibraryService.AddLocalFile(Outside("Again.zip", "old bytes"));

        LocalFile.All(Connection.Db).Should().ContainSingle();
        LibraryFile.DownloadPath.Get(local.AsLibraryFile()).ToString().Should().Be("Again.zip");
        FilesIn(Downloads).Should().Equal("Again.zip");
    }

    [Fact]
    public async Task AddLocalFile_SymlinkToFile_CopiesARegularFile()
    {
        var target = Outside("real.zip", "linked bytes");
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Link.zip");
        File.CreateSymbolicLink(link.ToString(), target.ToString());

        await LibraryService.AddLocalFile(link);

        var copy = Downloads.Combine("Link.zip");
        copy.FileExists.Should().BeTrue();
        new FileInfo(copy.ToString()).LinkTarget.Should().BeNull("Downloads must hold a regular file, never a link");
        File.ReadAllText(copy.ToString()).Should().Be("linked bytes");
        File.ReadAllText(target.ToString()).Should().Be("linked bytes");
    }

    [Fact]
    public async Task AddLocalFile_SymlinkToDirectory_IsRejected()
    {
        var dir = TemporaryFileManager.CreateFolder().Path;
        File.WriteAllText(dir.Combine("canary.txt").ToString(), "canary");
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Mod.zip");
        Directory.CreateSymbolicLink(link.ToString(), dir.ToString());

        var act = async () => await LibraryService.AddLocalFile(link);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{link}*");
        FilesIn(Downloads).Should().BeEmpty();
        LocalFile.All(Connection.Db).Should().BeEmpty();
        File.ReadAllText(dir.Combine("canary.txt").ToString()).Should().Be("canary");
    }

    [Fact]
    public async Task AddLocalFile_WithMetadata_StoresEverything()
    {
        var meta = new LocalFileMetadata(Name: "Mi mod", Version: "1.2.3", Source: "GitHub", PageUri: new Uri("https://github.com/x/y"));

        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), meta);

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mi mod");
        LocalFile.Version.Get(local).Should().Be("1.2.3");
        LocalFile.Source.Get(local).Should().Be("GitHub");
        LocalFile.PageUri.Get(local).Should().Be(new Uri("https://github.com/x/y"));
    }

    [Fact]
    public async Task AddLocalFile_EmptyMetadata_StoresNothing()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"));

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mod.zip");
        LocalFile.Version.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.Source.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.PageUri.TryGetValue(local, out _).Should().BeFalse();
    }

    [Fact]
    public async Task AddLocalFile_WhitespaceMetadata_StoresNothing()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), new LocalFileMetadata(Name: "  ", Version: "\t", Source: ""));

        local.AsLibraryFile().AsLibraryItem().Name.Should().Be("Mod.zip");
        LocalFile.Version.TryGetValue(local, out _).Should().BeFalse();
        LocalFile.Source.TryGetValue(local, out _).Should().BeFalse();
    }

    private static async Task<NexusMods.Hashing.xxHash3.Hash> HashOf(AbsolutePath path)
    {
        await using var stream = path.Read();
        return await NexusMods.Hashing.xxHash3.HashExtensions.xxHash3Async(stream);
    }
}
```

If `HashExtensions.xxHash3Async` is not the exact static name, use whatever `DownloadsFolder.HashOf` in `src/NexusMods.Sdk/Library/DownloadsFolder.cs` calls (`stream.xxHash3Async(token: ct)`); the extension lives in `NexusMods.Hashing.xxHash3`.

Append to `tests/NexusMods.DataModel.Tests/DownloadReExtractorTests.cs`:

```csharp
    [Fact]
    public async Task LocalFileAddedFromOutsideDownloads_RestoresFromTheCopy()
    {
        var library = ServiceProvider.GetRequiredService<ILibraryService>();
        var store = (LooseFileStore)ServiceProvider.GetRequiredService<IFileStore>();
        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var src = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("Lookup Anything 1.48.1-541-1-48-1-1739333325.zip");
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("outside.zip");
        File.Copy(src.ToString(), outside.ToString(), overwrite: true);

        var local = await library.AddLocalFile(outside);
        var entryHashes = LibraryArchiveFileEntry.FindByParent(Connection.Db, local.AsLibraryFile().Id)
            .Select(e => e.AsLibraryFile().Hash).ToArray();
        entryHashes.Should().NotBeEmpty();
        foreach (var h in entryHashes) store.PathFor(h).Delete();
        outside.Delete(); // the user's copy is gone; only Downloads/outside.zip remains

        var restored = await reExtractor.RestoreAsync(entryHashes, default);

        restored.Should().BeEquivalentTo(entryHashes);
        foreach (var h in entryHashes) (await store.HaveFile(h)).Should().BeTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~AddLocalFileTests|FullyQualifiedName~LocalFileAddedFromOutsideDownloads" 2>&1 | tail -25`
Expected: `AddLocalFile_InsideDownloads_IsNotCopied` and `EmptyMetadata` may pass already; every "copied", "suffix", "same hash", "symlink", "metadata" and the re-extractor test FAIL (no copy, no dedupe, metadata ignored).

- [ ] **Step 3: Implement the job**

Replace `src/NexusMods.Library/AddLocalFileJob.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Library.Jobs;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

internal class AddLocalFileJob : IJobDefinitionWithStart<AddLocalFileJob, LocalFile.ReadOnly>, IAddLocalFile
{
    public required AbsolutePath FilePath { get; init; }
    public required LocalFileMetadata Metadata { get; init; }
    internal required IConnection Connection { get; init; }
    internal required IServiceProvider ServiceProvider { get; init; }
    /// <summary>Root of the downloads folder. Not named DownloadsFolder: that is the static helper class.</summary>
    internal required AbsolutePath DownloadsRoot { get; init; }
    internal required ILogger<AddLocalFileJob> Logger { get; init; }

    public static IJobTask<AddLocalFileJob, LocalFile.ReadOnly> Create(IServiceProvider provider, AbsolutePath filePath, LocalFileMetadata metadata)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddLocalFileJob
        {
            FilePath = filePath,
            Metadata = metadata,
            Connection = provider.GetRequiredService<IConnection>(),
            ServiceProvider = provider,
            DownloadsRoot = provider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(provider.GetRequiredService<IFileSystem>()),
            Logger = provider.GetRequiredService<ILogger<AddLocalFileJob>>(),
        };
        return monitor.Begin<AddLocalFileJob, LocalFile.ReadOnly>(job);
    }

    public async ValueTask<LocalFile.ReadOnly> StartAsync(IJobContext<AddLocalFileJob> context)
    {
        var ct = context.CancellationToken;
        // File.Exists is false for directories and for symlinks to directories, so this also rejects a
        // link to a folder before anything is copied. A symlink to a file passes and is copied as a
        // regular file by PlaceAsync (it reads the content, it never links).
        if (!FilePath.FileExists)
            throw new InvalidOperationException($"No es un archivo o no existe: {FilePath}");

        // 1. Copy into Downloads unless it already lives there. The user's file is never moved.
        var final = FilePath.InFolder(DownloadsRoot)
            ? FilePath
            : await DownloadsFolder.PlaceAsync(FilePath, DownloadsRoot, FilePath.FileName, ct);
        var weCopied = final != FilePath;

        // 2. Same content already in the library as a local file: reuse it.
        var hash = await HashOf(final, ct);
        var existing = LibraryFile.FindByHash(Connection.Db, hash)
            .Select(file => LocalFile.Load(Connection.Db, file.Id))
            .FirstOrDefault(local => local.IsValid());
        if (existing.IsValid())
            return await ReuseExisting(existing, final, weCopied, ct);

        // 3. Register the copy. AddLibraryFileJob records DownloadPath because `final` is inside Downloads.
        using var tx = Connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, final);
        var localFile = new LocalFile.New(tx, libraryFile.LibraryFileId)
        {
            LibraryFile = libraryFile,
            OriginalPath = FilePath.ToString(),
        };
        ApplyMetadata(tx, libraryFile.Id, Metadata);
        var result = await tx.Commit();
        return result.Remap(localFile);
    }

    private async ValueTask<LocalFile.ReadOnly> ReuseExisting(LocalFile.ReadOnly existing, AbsolutePath final, bool weCopied, CancellationToken ct)
    {
        var libraryFile = existing.AsLibraryFile();
        var hasDownload = LibraryFile.DownloadPath.TryGetValue(libraryFile, out var rel) && DownloadsRoot.Combine(rel).FileExists;
        using var tx = Connection.BeginTransaction();
        if (hasDownload)
        {
            // The library already has a download for this content; our copy is redundant unless
            // PlaceAsync handed us that very file (same name + same content reuses it).
            if (weCopied && final != DownloadsRoot.Combine(rel)) final.Delete();
            Logger.LogInformation("'{File}' ya estaba en la biblioteca como '{Existing}'", FilePath, libraryFile.AsLibraryItem().Name);
        }
        else
        {
            // Old LocalFile registered from outside Downloads: adopt this copy as its download.
            tx.Add(existing.Id, LibraryFile.DownloadPath, final.RelativeTo(DownloadsRoot));
            Logger.LogInformation("'{File}' repara la descarga faltante de '{Existing}'", final, libraryFile.AsLibraryItem().Name);
        }
        ApplyMetadata(tx, existing.Id, Metadata);
        await tx.Commit();
        return LocalFile.Load(Connection.Db, existing.Id);
    }

    /// <summary>
    /// Writes the non-blank fields of <paramref name="metadata"/>. Blank values are skipped, never stored.
    /// </summary>
    internal static void ApplyMetadata(ITransaction tx, EntityId id, LocalFileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Name)) tx.Add(id, LibraryItem.Name, metadata.Name.Trim());
        if (!string.IsNullOrWhiteSpace(metadata.Version)) tx.Add(id, LocalFile.Version, metadata.Version.Trim());
        if (!string.IsNullOrWhiteSpace(metadata.Source)) tx.Add(id, LocalFile.Source, metadata.Source.Trim());
        if (metadata.PageUri is not null) tx.Add(id, LocalFile.PageUri, metadata.PageUri);
    }

    private static async Task<Hash> HashOf(AbsolutePath path, CancellationToken ct)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async(token: ct);
    }
}
```

Notes for the implementer:
- `tx.Commit()` is only reached in `ReuseExisting` when there is something to write; an empty transaction commit is harmless in MnemonicDB, keep it simple.
- `LocalFile.Load(db, id).IsValid()` is true only when the entity has `LocalFile.OriginalPath`; nested `LibraryArchiveFileEntry` rows with the same hash are therefore skipped.
- Do not touch `AddLibraryFileJob`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~AddLocalFileTests|FullyQualifiedName~DownloadReExtractorTests" 2>&1 | tail -8`
Expected: all PASS.

- [ ] **Step 5: Temporarily revert the copy to prove the symlink tests bite**

Comment out the `? FilePath : await DownloadsFolder.PlaceAsync(...)` branch so `final = FilePath` always, run `--filter "FullyQualifiedName~Symlink"`: both symlink tests plus `OutsideDownloads_IsCopiedAndRegistered` must FAIL. Restore the code, rerun, PASS. (The directory-symlink test also fails if the `FileExists` guard is removed: `AddLibraryFileJob` throws a plain `Exception`, not `InvalidOperationException`.)

- [ ] **Step 6: Run the whole DataModel test project and the Collections project (they call `AddLocalFile`)**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "RequiresNetworking!=True&FlakeyTest!=True" 2>&1 | tail -5 && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.Collections.Tests --filter "RequiresNetworking!=True&FlakeyTest!=True" 2>&1 | tail -5`
Expected: all PASS, no `.verified.` changes (`git status` shows only the files from this task).

- [ ] **Step 7: Commit**

```bash
git add src/NexusMods.Library/AddLocalFileJob.cs tests/NexusMods.DataModel.Tests/AddLocalFileTests.cs tests/NexusMods.DataModel.Tests/DownloadReExtractorTests.cs
git commit -m "feat(library): copy hand-added files into Downloads and dedupe by hash"
```

---

### Task 3: Version parser from the file name (Sdk)

**Files:**
- Create: `src/NexusMods.Sdk/Library/LocalFileNameParser.cs`
- Create: `tests/NexusMods.Sdk.Tests/LocalFileNameParserTests.cs`

**Interfaces:**
- Produces: `static string? LocalFileNameParser.TryParseVersion(string fileName)` and `static string DisplayName(string fileName)` (file name without extension; `.tar.gz` style double extensions are not handled, YAGNI).

- [ ] **Step 1: Write the failing TUnit tests**

`tests/NexusMods.Sdk.Tests/LocalFileNameParserTests.cs`:

```csharp
using NexusMods.Sdk.Library;

namespace NexusMods.Sdk.Tests;

public class LocalFileNameParserTests
{
    [Test]
    [Arguments("Mod-1.2.3.zip", "1.2.3")]
    [Arguments("Mod_v1.2.zip", "1.2")]
    [Arguments("Mod v2.0.1 (fixed).7z", "2.0.1")]
    [Arguments("Mod V3.zip", "3")]
    [Arguments("CET-1.35.1.zip", "1.35.1")]
    [Arguments("mod 0.9.zip", "0.9")]
    public async Task TryParseVersion_FindsTheVersion(string fileName, string expected)
    {
        await Assert.That(LocalFileNameParser.TryParseVersion(fileName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Mod.zip")]
    [Arguments("Mod-123-1-0-1757.zip")] // Nexus naming: mod id + version with dashes + timestamp; not guessed
    [Arguments("2077.zip")]
    [Arguments("")]
    public async Task TryParseVersion_LeavesUnknownAlone(string fileName)
    {
        await Assert.That(LocalFileNameParser.TryParseVersion(fileName)).IsNull();
    }

    [Test]
    [Arguments("Mod-1.2.3.zip", "Mod-1.2.3")]
    [Arguments("archive.tar", "archive")]
    [Arguments("noext", "noext")]
    public async Task DisplayName_DropsTheExtension(string fileName, string expected)
    {
        await Assert.That(LocalFileNameParser.DisplayName(fileName)).IsEqualTo(expected);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet run --project tests/NexusMods.Sdk.Tests -- --treenode-filter "/*/*/LocalFileNameParserTests/*" 2>&1 | tail -5`
Expected: build error, `LocalFileNameParser` does not exist.

- [ ] **Step 3: Implement**

`src/NexusMods.Sdk/Library/LocalFileNameParser.cs`:

```csharp
using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Guesses display name and version from a hand-picked file name. Only used to prefill a dialog;
/// the user can always correct it.
/// </summary>
[PublicAPI]
public static partial class LocalFileNameParser
{
    // A version is "v" or a separator, then digits with at least one dot ("1.2", "1.2.3") or a bare
    // number right after "v" ("V3"). Nexus names ("Mod-123-1-0-1757") use dashes, not dots, so they
    // are left alone on purpose: "123" there is the mod id, not a version.
    [GeneratedRegex(@"(?:^|[\s_\-(])[vV]?(\d+(?:\.\d+)+)(?=$|[\s_\-).\]])|(?:^|[\s_\-(])[vV](\d+)(?=$|[\s_\-).\]])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    public static string? TryParseVersion(string fileName)
    {
        var stem = DisplayName(fileName);
        if (stem.Length == 0) return null;
        var match = VersionRegex().Match(stem);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        // A stem that is only the number ("2077") is a name, not a version.
        return value == stem ? null : value;
    }

    public static string DisplayName(string fileName) => Path.GetFileNameWithoutExtension(fileName);
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet run --project tests/NexusMods.Sdk.Tests -- --treenode-filter "/*/*/LocalFileNameParserTests/*" 2>&1 | tail -5`
Expected: all PASS. If a case fails, adjust the regex, not the test table (the table is the contract). `"mod 0.9.zip"` needs the leading-space alternative; `"2077.zip"` is caught by the `value == stem` guard.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Sdk/Library/LocalFileNameParser.cs tests/NexusMods.Sdk.Tests/LocalFileNameParserTests.cs
git commit -m "feat(sdk): guess name and version from a hand-picked file name"
```

---

### Task 4: Edit metadata through the service

**Files:**
- Modify: `src/NexusMods.Abstractions.Library/ILibraryService.cs`
- Modify: `src/NexusMods.Library/LibraryService.cs`
- Modify: `tests/NexusMods.DataModel.Tests/AddLocalFileTests.cs` (append tests)

**Interfaces:**
- Consumes: `AddLocalFileJob.ApplyMetadata` is NOT reused here (edit needs retractions); `LocalFile.Version/Source/PageUri` (Task 1).
- Produces: `Task ILibraryService.UpdateLocalFileMetadata(LocalFileId id, LocalFileMetadata metadata)`: sets non-blank fields, retracts blank ones; `Name` blank → falls back to `LibraryFile.FileName`.

- [ ] **Step 1: Write the failing tests**

Append to `AddLocalFileTests`:

```csharp
    [Fact]
    public async Task UpdateLocalFileMetadata_SetsAndClearsFields()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), new LocalFileMetadata(Version: "1.0", Source: "foro"));

        await LibraryService.UpdateLocalFileMetadata(local.LocalFileId, new LocalFileMetadata(Name: "Nuevo", Version: "2.0", Source: "", PageUri: new Uri("https://example.org/mod")));

        var updated = LocalFile.Load(Connection.Db, local.Id);
        updated.AsLibraryFile().AsLibraryItem().Name.Should().Be("Nuevo");
        LocalFile.Version.Get(updated).Should().Be("2.0");
        LocalFile.Source.TryGetValue(updated, out _).Should().BeFalse("a blank value clears the field");
        LocalFile.PageUri.Get(updated).Should().Be(new Uri("https://example.org/mod"));
    }

    [Fact]
    public async Task UpdateLocalFileMetadata_BlankName_FallsBackToFileName()
    {
        var local = await LibraryService.AddLocalFile(Outside("Mod.zip"), new LocalFileMetadata(Name: "Con nombre"));

        await LibraryService.UpdateLocalFileMetadata(local.LocalFileId, new LocalFileMetadata(Name: "   "));

        LocalFile.Load(Connection.Db, local.Id).AsLibraryFile().AsLibraryItem().Name.Should().Be("Mod.zip");
    }

    [Fact]
    public async Task UpdateLocalFileMetadata_DoesNotRenameInstalledGroup()
    {
        var src = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("Lookup Anything 1.48.1-541-1-48-1-1739333325.zip");
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("Lookup.zip");
        File.Copy(src.ToString(), outside.ToString(), overwrite: true);
        var loadout = await CreateLoadout();
        var local = await LibraryService.AddLocalFile(outside);
        var installed = await LoadoutManager.InstallItem(local.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        var groupId = installed.LoadoutItemGroup!.Value.Id;
        var groupName = installed.LoadoutItemGroup!.Value.AsLoadoutItem().Name;

        await LibraryService.UpdateLocalFileMetadata(local.LocalFileId, new LocalFileMetadata(Name: "Renombrado"));

        LoadoutItemGroup.Load(Connection.Db, groupId).AsLoadoutItem().Name.Should().Be(groupName);
    }
```

`CreateLoadout()` is `protected` on `AIsolatedGameTest` (`tests/Games/NexusMods.Games.TestFramework/AIsolatedGameTest.cs:373`). `InstallItem` returns `InstallLoadoutItemJobResult(LoadoutItemGroup.ReadOnly? LoadoutItemGroup, LoadoutItemGroupId GroupTxId)`. Add `using NexusMods.Abstractions.Loadouts;` for `LoadoutItemGroup`.

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~UpdateLocalFileMetadata" 2>&1 | tail -5`
Expected: build error, `UpdateLocalFileMetadata` does not exist.

- [ ] **Step 3: Implement**

`ILibraryService.cs`, after `AddLocalFile`:

```csharp
    /// <summary>
    /// Replaces the metadata of a local file: non-blank fields are written, blank ones are cleared.
    /// A blank name falls back to the file name. Loadout groups already installed from it keep their name.
    /// </summary>
    Task UpdateLocalFileMetadata(LocalFileId id, LocalFileMetadata metadata);
```

`LibraryService.cs`:

```csharp
    public async Task UpdateLocalFileMetadata(LocalFileId id, LocalFileMetadata metadata)
    {
        var db = _connection.Db;
        var local = LocalFile.Load(db, id);
        if (!local.IsValid()) throw new InvalidOperationException($"No existe el archivo local {id}");

        using var tx = _connection.BeginTransaction();
        var name = string.IsNullOrWhiteSpace(metadata.Name) ? local.AsLibraryFile().FileName.ToString() : metadata.Name.Trim();
        if (name != local.AsLibraryFile().AsLibraryItem().Name) tx.Add(id, LibraryItem.Name, name);
        SetOrRetract(tx, id, LocalFile.Version, local, metadata.Version);
        SetOrRetract(tx, id, LocalFile.Source, local, metadata.Source);
        if (metadata.PageUri is null)
        {
            if (LocalFile.PageUri.TryGetValue(local, out var old)) tx.Retract(id, LocalFile.PageUri, old);
        }
        else if (!LocalFile.PageUri.TryGetValue(local, out var current) || current != metadata.PageUri)
        {
            tx.Add(id, LocalFile.PageUri, metadata.PageUri);
        }
        await tx.Commit();
    }

    private static void SetOrRetract(ITransaction tx, EntityId id, StringAttribute attribute, LocalFile.ReadOnly local, string? value)
    {
        var has = attribute.TryGetValue(local, out var old);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (has) tx.Retract(id, attribute, old);
            return;
        }
        var trimmed = value.Trim();
        if (!has || old != trimmed) tx.Add(id, attribute, trimmed);
    }
```

`LibraryService` already has `_connection` (check the field name at the top of the class; if it is `_conn`, use that). `LocalFileId` implicitly converts to `EntityId`; if `tx.Add(id, ...)` does not accept it, pass `id.Value`.

- [ ] **Step 4: Run to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~AddLocalFileTests" 2>&1 | tail -5`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Abstractions.Library/ILibraryService.cs src/NexusMods.Library/LibraryService.cs tests/NexusMods.DataModel.Tests/AddLocalFileTests.cs
git commit -m "feat(library): edit name, version, source and page of a local file"
```

---

### Task 5: Backfill of old local files at startup

**Files:**
- Create: `src/NexusMods.Library/LocalFileBackfill.cs`
- Modify: `src/NexusMods.Library/Services.cs`
- Create: `tests/NexusMods.DataModel.Tests/LocalFileBackfillTests.cs`

**Interfaces:**
- Produces: `public sealed class LocalFileBackfill : IHostedService` with `public Task<int> RunAsync(CancellationToken ct)` returning how many files were copied. `StartAsync` runs `RunAsync` in the background (`Task.Run`) and logs failures; it never blocks startup.

- [ ] **Step 1: Write the failing tests**

`tests/NexusMods.DataModel.Tests/LocalFileBackfillTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using NexusMods.Library;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class LocalFileBackfillTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LocalFileBackfillTests>(helper)
{
    private AbsolutePath Downloads => ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);

    private async Task<LocalFile.ReadOnly> OldLocalFile(AbsolutePath original)
    {
        using var tx = Connection.BeginTransaction();
        var lib = new LibraryFile.New(tx, out var id)
        {
            FileName = original.FileName,
            Hash = NexusMods.Hashing.xxHash3.Hash.From(42),
            Size = Size.From(1),
            LibraryItem = new LibraryItem.New(tx, id) { Name = original.FileName },
        };
        var local = new LocalFile.New(tx, id) { LibraryFile = lib, OriginalPath = original.ToString() };
        var result = await tx.Commit();
        return result.Remap(local);
    }

    [Fact]
    public async Task OriginalPresent_CopiesAndSetsDownloadPath()
    {
        var original = TemporaryFileManager.CreateFolder().Path.Combine("Old.zip");
        File.WriteAllText(original.ToString(), "old");
        var local = await OldLocalFile(original);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(1);
        Downloads.Combine("Old.zip").FileExists.Should().BeTrue();
        original.FileExists.Should().BeTrue();
        LibraryFile.DownloadPath.Get(LocalFile.Load(Connection.Db, local.Id).AsLibraryFile()).ToString().Should().Be("Old.zip");
    }

    [Fact]
    public async Task OriginalMissing_LeavesItemUntouched()
    {
        var local = await OldLocalFile(TemporaryFileManager.CreateFolder().Path.Combine("Gone.zip"));

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        LibraryFile.DownloadPath.TryGetValue(LocalFile.Load(Connection.Db, local.Id).AsLibraryFile(), out _).Should().BeFalse();
        Downloads.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task OriginalIsASymlinkToADirectory_IsSkipped()
    {
        var dir = TemporaryFileManager.CreateFolder().Path;
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Dir.zip");
        Directory.CreateSymbolicLink(link.ToString(), dir.ToString());
        await OldLocalFile(link);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        Downloads.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task AlreadyHasDownloadPath_IsSkipped()
    {
        Downloads.CreateDirectory();
        var inside = Downloads.Combine("Have.zip");
        File.WriteAllText(inside.ToString(), "x");
        await LibraryService.AddLocalFile(inside);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        Downloads.EnumerateFiles("*", recursive: false).Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~LocalFileBackfillTests" 2>&1 | tail -5`
Expected: build error, `LocalFileBackfill` does not exist.

- [ ] **Step 3: Implement**

`src/NexusMods.Library/LocalFileBackfill.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

/// <summary>
/// One-time repair for local files registered before hand-added files were copied into Downloads:
/// copies each one whose original still exists and records its DownloadPath. Idempotent; a run
/// that stops halfway finishes on the next start.
/// </summary>
public sealed class LocalFileBackfill(IConnection connection, ISettingsManager settings, IFileSystem fileSystem, ILogger<LocalFileBackfill> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await RunAsync(cancellationToken); }
            catch (Exception e) { logger.LogWarning(e, "No se pudieron copiar los archivos locales viejos a Descargas"); }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var downloads = settings.Get<DownloadsSettings>().Folder.ToPath(fileSystem);
        var copied = 0;
        foreach (var local in LocalFile.All(connection.Db).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var libraryFile = local.AsLibraryFile();
            if (LibraryFile.DownloadPath.TryGetValue(libraryFile, out _)) continue;

            var original = fileSystem.FromUnsanitizedFullPath(local.OriginalPath);
            // File.Exists is false for directories and for links to directories.
            if (!original.FileExists)
            {
                logger.LogInformation("Archivo local '{Name}' sin descarga: el original '{Path}' ya no está", libraryFile.AsLibraryItem().Name, local.OriginalPath);
                continue;
            }

            var placed = await DownloadsFolder.PlaceAsync(original, downloads, original.FileName, ct);
            using var tx = connection.BeginTransaction();
            tx.Add(local.Id, LibraryFile.DownloadPath, placed.RelativeTo(downloads));
            await tx.Commit();
            copied++;
            logger.LogInformation("Archivo local '{Name}' copiado a Descargas como '{File}'", libraryFile.AsLibraryItem().Name, placed.FileName);
        }
        return copied;
    }
}
```

`ExternalDownloadJob` stores a bare file name in `OriginalPath` for collection downloads (`FileName.Value.ToString()`), but those always have `DownloadPath`, so the `continue` on the first check skips them before `FromUnsanitizedFullPath` sees a relative string. If `FromUnsanitizedFullPath` throws on a relative path anyway, wrap it: `if (!Path.IsPathRooted(local.OriginalPath)) continue;` before it.

`src/NexusMods.Library/Services.cs`:

```csharp
            .AddSingleton<IDownloadReExtractor, DownloadReExtractor>()
            .AddSingleton<IHostedService, LocalFileBackfill>();
```

Add `using Microsoft.Extensions.Hosting;`. `NexusMods.Library.csproj` does not reference the hosting package yet: add `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />` to its `<ItemGroup>` (no `Version=`; `Directory.Packages.props:74` already pins 10.0.12, the same version `NexusMods.DataModel.csproj:25` uses, so no new package enters the solution).

- [ ] **Step 4: Run to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~LocalFileBackfillTests" 2>&1 | tail -5`
Expected: all PASS.

- [ ] **Step 5: Confirm startup wiring compiles and the app's hosted services still start**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | tail -3`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.Library/LocalFileBackfill.cs src/NexusMods.Library/Services.cs src/NexusMods.Library/NexusMods.Library.csproj tests/NexusMods.DataModel.Tests/LocalFileBackfillTests.cs
git commit -m "feat(library): copy old hand-added files into Downloads at startup"
```

---

### Task 6: CLI options

**Files:**
- Modify: `src/NexusMods.DataModel/CommandLine/Verbs/LoadoutManagementVerbs.cs:126-140`

**Interfaces:**
- Consumes: `ILibraryService.AddLocalFile(path, LocalFileMetadata)` (Task 1).

- [ ] **Step 1: Add the options**

Replace the `InstallMod` verb with:

```csharp
    [Verb("loadout install", "Installs a mod into a loadout")]
    private static async Task<int> InstallMod([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Option("f", "file", "Mod file to install")] AbsolutePath file,
        [Option("n", "name", "Name of the mod after installing")] string name,
        [Option("v", "version", "Version to record for the file", isOptional: true)] string? version,
        [Option("s", "source", "Where the file came from (mod.io, GitHub, foro...)", isOptional: true)] string? source,
        [Option("u", "url", "Page of the mod", isOptional: true)] Uri? url,
        [Injected] ILibraryService libraryService,
        [Injected] ILoadoutManager loadoutManager,
        [Injected] CancellationToken token)
    {
        return await renderer.WithProgress(token, async () =>
        {
            var localFile = await libraryService.AddLocalFile(file, new LocalFileMetadata(Name: name, Version: version, Source: source, PageUri: url));
            await loadoutManager.InstallItem(localFile.AsLibraryFile().AsLibraryItem(), loadout);
            return 0;
        });
    }
```

If the CLI option parser has no `IOptionParser<Uri>` (check `src/NexusMods.App.Cli` and `src/NexusMods.Sdk/ProxyConsole` for `IOptionParser<`), take `string? url` and convert with `Uri.TryCreate(url, UriKind.Absolute, out var pageUri) ? pageUri : null`. Add `using NexusMods.Abstractions.Library;`.

Note: `name` was already a parameter that the old code ignored; it now becomes the display name.

- [ ] **Step 2: Build and smoke-test the help**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | tail -3 && DOTNET_ROLL_FORWARD=Major dotnet run --project src/NexusMods.App -- loadout install --help 2>&1 | tail -15`
Expected: build clean; help lists `--version`, `--source`, `--url` as optional. If running the CLI needs a running main process and refuses, the build is enough.

- [ ] **Step 3: Commit**

```bash
git add src/NexusMods.DataModel/CommandLine/Verbs/LoadoutManagementVerbs.cs
git commit -m "feat(cli): version, source and url options for loadout install"
```

---

### Task 7: Metadata overlay (UI)

**Files:**
- Create: `src/NexusMods.App.UI/Overlays/LocalFileMetadata/ILocalFileMetadataOverlayViewModel.cs`
- Create: `src/NexusMods.App.UI/Overlays/LocalFileMetadata/LocalFileMetadataOverlayViewModel.cs`
- Create: `src/NexusMods.App.UI/Overlays/LocalFileMetadata/LocalFileMetadataOverlayDesignViewModel.cs`
- Create: `src/NexusMods.App.UI/Overlays/LocalFileMetadata/LocalFileMetadataOverlayView.axaml`
- Create: `src/NexusMods.App.UI/Overlays/LocalFileMetadata/LocalFileMetadataOverlayView.axaml.cs`
- Modify: `src/NexusMods.App.UI/Services.cs:229-230` (register next to `ManualAddGame`)

**Interfaces:**
- Consumes: `LocalFileMetadata` (Task 1), `LocalFileNameParser` (Task 3).
- Produces: `record struct LocalFileMetadataOverlayResult(bool Confirmed, LocalFileMetadata Metadata)` with `static Cancel`; `LocalFileMetadataOverlayViewModel(string title, string acceptText, string originPath, LocalFileMetadata initial)`; `static LocalFileMetadata LocalFileMetadataOverlayViewModel.PrefillFor(string fileName)`.

- [ ] **Step 1: Interface and result**

`ILocalFileMetadataOverlayViewModel.cs`:

```csharp
using NexusMods.Abstractions.Library;
using R3;

namespace NexusMods.App.UI.Overlays;

public record struct LocalFileMetadataOverlayResult(bool Confirmed, LocalFileMetadata Metadata)
{
    public static readonly LocalFileMetadataOverlayResult Cancel = new(Confirmed: false, Metadata: LocalFileMetadata.Empty);
}

public interface ILocalFileMetadataOverlayViewModel : IOverlayViewModel<LocalFileMetadataOverlayResult>
{
    string Title { get; }
    string AcceptText { get; }
    /// <summary>Where the file was picked from, or its recorded OriginalPath when editing. Read-only text.</summary>
    string OriginPath { get; }
    BindableReactiveProperty<string> Name { get; }
    BindableReactiveProperty<string> Version { get; }
    BindableReactiveProperty<string> Source { get; }
    BindableReactiveProperty<string> PageUrl { get; }
    IReadOnlyBindableReactiveProperty<bool> IsUrlValid { get; }
    ReactiveCommand<Unit> CommandCancel { get; }
    ReactiveCommand<Unit> CommandAccept { get; }
}
```

- [ ] **Step 2: View model and design view model**

`LocalFileMetadataOverlayViewModel.cs`:

```csharp
using NexusMods.Abstractions.Library;
using NexusMods.Sdk.Library;
using R3;

namespace NexusMods.App.UI.Overlays;

public class LocalFileMetadataOverlayViewModel : AOverlayViewModel<ILocalFileMetadataOverlayViewModel, LocalFileMetadataOverlayResult>, ILocalFileMetadataOverlayViewModel
{
    public string Title { get; }
    public string AcceptText { get; }
    public string OriginPath { get; }
    public BindableReactiveProperty<string> Name { get; }
    public BindableReactiveProperty<string> Version { get; }
    public BindableReactiveProperty<string> Source { get; }
    public BindableReactiveProperty<string> PageUrl { get; }
    public IReadOnlyBindableReactiveProperty<bool> IsUrlValid { get; }
    public ReactiveCommand<Unit> CommandCancel { get; }
    public ReactiveCommand<Unit> CommandAccept { get; }

    public LocalFileMetadataOverlayViewModel(string title, string acceptText, string originPath, LocalFileMetadata initial)
    {
        Title = title;
        AcceptText = acceptText;
        OriginPath = originPath;
        Name = new BindableReactiveProperty<string>(initial.Name ?? string.Empty);
        Version = new BindableReactiveProperty<string>(initial.Version ?? string.Empty);
        Source = new BindableReactiveProperty<string>(initial.Source ?? string.Empty);
        PageUrl = new BindableReactiveProperty<string>(initial.PageUri?.ToString() ?? string.Empty);

        var urlValid = PageUrl.Select(static text => TryParseUrl(text, out _));
        IsUrlValid = urlValid.ToReadOnlyBindableReactiveProperty(initialValue: true);

        CommandCancel = new ReactiveCommand(_ => Complete(result: LocalFileMetadataOverlayResult.Cancel));
        CommandAccept = urlValid.ToReactiveCommand<Unit>(_ =>
        {
            TryParseUrl(PageUrl.Value, out var uri);
            Complete(result: new LocalFileMetadataOverlayResult(Confirmed: true, Metadata: new LocalFileMetadata(Name.Value, Version.Value, Source.Value, uri)));
        }, initialCanExecute: true);
    }

    /// <summary>Empty text is valid (no URL); anything else must be an absolute http(s) URL.</summary>
    private static bool TryParseUrl(string text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";
    }

    /// <summary>What the "add" dialog starts with: the file name without extension, and the version if the name carries one.</summary>
    public static LocalFileMetadata PrefillFor(string fileName) =>
        new(Name: LocalFileNameParser.DisplayName(fileName), Version: LocalFileNameParser.TryParseVersion(fileName));
}
```

If `ToReactiveCommand<Unit>(Action<Unit>, bool)` does not match the R3 overload set, use the form the sibling view models use: `urlValid.ToReactiveCommand<Unit>(execute: _ => {...}, initialCanExecute: true)`; look at `LibraryViewModel.cs:172` for the `executeAsync` variant and adapt.

`LocalFileMetadataOverlayDesignViewModel.cs`:

```csharp
using NexusMods.Abstractions.Library;

namespace NexusMods.App.UI.Overlays;

public class LocalFileMetadataOverlayDesignViewModel() : LocalFileMetadataOverlayViewModel(
    title: "Agregar archivo",
    acceptText: "Agregar",
    originPath: "/home/usuario/Descargas/Better Minimap-1.2.3.zip",
    initial: new LocalFileMetadata(Name: "Better Minimap", Version: "1.2.3", Source: "GitHub", PageUri: new Uri("https://github.com/x/better-minimap")));
```

- [ ] **Step 3: View**

`LocalFileMetadataOverlayView.axaml` (same skeleton as `ManualAddGameOverlayView.axaml`):

```xml
<reactive:ReactiveUserControl
    x:TypeArguments="local:ILocalFileMetadataOverlayViewModel"
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    xmlns:reactive="http://reactiveui.net"
    xmlns:local="clr-namespace:NexusMods.App.UI.Overlays"
    xmlns:base="clr-namespace:NexusMods.App.UI.Overlays.Generic.MessageBox.Base"
    xmlns:controls="clr-namespace:NexusMods.App.UI.Controls"
    x:DataType="local:ILocalFileMetadataOverlayViewModel"
    mc:Ignorable="d" d:DesignWidth="600" d:DesignHeight="460"
    x:Class="NexusMods.App.UI.Overlays.LocalFileMetadataOverlayView">

    <Design.DataContext>
        <local:LocalFileMetadataOverlayDesignViewModel />
    </Design.DataContext>

    <base:MessageBoxBackground MinWidth="560" MaxWidth="560">
        <base:MessageBoxBackground.TopContent>
            <Border Padding="24">
                <StackPanel Spacing="16">
                    <TextBlock Text="{CompiledBinding Title}" TextWrapping="Wrap" Theme="{StaticResource HeadingXSSemiTheme}" />
                    <TextBlock Text="{CompiledBinding OriginPath}" TextWrapping="Wrap" Theme="{StaticResource BodySMNormalTheme}" Opacity="0.7" />

                    <StackPanel Spacing="8">
                        <TextBlock Text="Nombre" Theme="{StaticResource BodySMNormalTheme}" Opacity="0.7" />
                        <TextBox x:Name="TextBoxName" Text="{CompiledBinding Name.Value}" />
                    </StackPanel>
                    <StackPanel Spacing="8">
                        <TextBlock Text="Versión (opcional)" Theme="{StaticResource BodySMNormalTheme}" Opacity="0.7" />
                        <TextBox Text="{CompiledBinding Version.Value}" />
                    </StackPanel>
                    <StackPanel Spacing="8">
                        <TextBlock Text="Fuente (opcional: mod.io, GitHub, foro...)" Theme="{StaticResource BodySMNormalTheme}" Opacity="0.7" />
                        <TextBox Text="{CompiledBinding Source.Value}" />
                    </StackPanel>
                    <StackPanel Spacing="8">
                        <TextBlock Text="Página del mod (opcional)" Theme="{StaticResource BodySMNormalTheme}" Opacity="0.7" />
                        <TextBox Text="{CompiledBinding PageUrl.Value}" Watermark="https://" />
                        <TextBlock Text="La URL tiene que empezar con http:// o https://"
                                   IsVisible="{CompiledBinding !IsUrlValid.Value}"
                                   Theme="{StaticResource BodySMNormalTheme}"
                                   Classes="Danger" />
                    </StackPanel>
                </StackPanel>
            </Border>
        </base:MessageBoxBackground.TopContent>

        <base:MessageBoxBackground.BottomContent>
            <StackPanel Orientation="Horizontal" Margin="24" VerticalAlignment="Center" HorizontalAlignment="Right" Spacing="{StaticResource Spacing-2.5}">
                <controls:StandardButton x:Name="ButtonCancel" Text="Cancelar" />
                <controls:StandardButton x:Name="ButtonAccept" Text="{CompiledBinding AcceptText}" Type="Primary" Fill="StrongAlt" IsDefault="True" />
            </StackPanel>
        </base:MessageBoxBackground.BottomContent>
    </base:MessageBoxBackground>
</reactive:ReactiveUserControl>
```

`IsDefault="True"` makes Enter accept. If `StandardButton` does not expose `IsDefault`, drop it and add in code-behind a `KeyDown` handler on the control: `Key.Enter` → `ViewModel?.CommandAccept.Execute(Unit.Default)`, `Key.Escape` → `CommandCancel`. If `Classes="Danger"` is not a known TextBlock class, use `Foreground="{StaticResource DangerStrongBrush}"` or whatever brush `LegacyCleanupOverlayView.axaml` uses for warnings.

`LocalFileMetadataOverlayView.axaml.cs`:

```csharp
using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays;

public partial class LocalFileMetadataOverlayView : ReactiveUserControl<ILocalFileMetadataOverlayViewModel>
{
    public LocalFileMetadataOverlayView()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandCancel, v => v.ButtonCancel).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CommandAccept, v => v.ButtonAccept).DisposeWith(disposables);
            TextBoxName.Focus();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { } vm)
        {
            vm.CommandCancel.Execute(Unit.Default);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
```

- [ ] **Step 4: Register**

`src/NexusMods.App.UI/Services.cs`, after the `ManualAddGame` pair:

```csharp
            .AddView<LocalFileMetadataOverlayView, ILocalFileMetadataOverlayViewModel>()
            .AddViewModel<LocalFileMetadataOverlayViewModel, ILocalFileMetadataOverlayViewModel>()
```

If `AddViewModel` requires a parameterless DI constructor and the registration fails at startup, drop only the `AddViewModel` line: the overlay is constructed by hand with `new`, like `ManualAddGameOverlayViewModel` in `MyGamesViewModel.cs:130`; the `AddView` line is what the view locator needs.

- [ ] **Step 5: Build**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | grep -E "error|warn|Warn|Error" | grep -v NU19 | head; echo BUILD-DONE`
Expected: nothing but `BUILD-DONE` (XAML compile errors show here).

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.App.UI/Overlays/LocalFileMetadata src/NexusMods.App.UI/Services.cs
git commit -m "feat(ui): dialog for name, version, source and page of a local file"
```

---

### Task 8: Library page: dialog on add, "Editar" button, version column, mod page link

**Files:**
- Modify: `src/NexusMods.App.UI/Pages/LibraryPage/LibraryViewModel.cs` (`AddFilesFromDisk` ~line 893, commands ~216, `HandleViewModPageMessage` 704, `ViewModPageMessage` 1027, `GetModPageIdOneOfType` 1095)
- Modify: `src/NexusMods.App.UI/Pages/LibraryPage/ILibraryViewModel.cs`
- Modify: `src/NexusMods.App.UI/Pages/LibraryPage/LibraryView.axaml` (toolbar, after `RemoveModButton`)
- Modify: `src/NexusMods.App.UI/Pages/LibraryPage/LibraryView.axaml.cs` (bind the new button)
- Modify: `src/NexusMods.App.UI/Pages/LocalFileDataProvider.cs` (`SetupLibraryItemModel`)

**Interfaces:**
- Consumes: `LocalFileMetadataOverlayViewModel`, `LocalFileMetadataOverlayResult`, `PrefillFor` (Task 7); `ILibraryService.UpdateLocalFileMetadata` (Task 4); `IOverlayController.EnqueueAndWait` (exists).
- Produces: `ILibraryViewModel.EditLocalFileMetadataCommand`; `ViewModPageMessage(OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId, LocalFileId> Id)`.

Design note: `LibraryView` has no right-click context menu; every per-selection action is a toolbar button or flyout. "Editar" therefore goes in the toolbar next to the delete button, enabled only when the selection is exactly one local file. This is the spec's "Editar metadata" with the only placement the page supports.

- [ ] **Step 1: Dialog on add**

In `LibraryViewModel.AddFilesFromDisk`, replace the `Parallel.ForAsync(...)` block with:

```csharp
        var overlayController = _serviceProvider.GetRequiredService<IOverlayController>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var overlay = new LocalFileMetadataOverlayViewModel(
                title: "Agregar archivo a la biblioteca",
                acceptText: "Agregar",
                originPath: path.ToString(),
                initial: LocalFileMetadataOverlayViewModel.PrefillFor(path.FileName));
            var result = await overlayController.EnqueueAndWait(overlay);
            if (!result.Confirmed) continue;

            try
            {
                await _libraryService.AddLocalFile(path, result.Metadata);
            }
            catch (Exception ex)
            {
                _serviceProvider.GetRequiredService<ILogger<LibraryViewModel>>().LogError(ex, "No se pudo agregar '{Path}'", path);
                _notificationService.ShowToast($"No se agregó '{path.FileName}': {ex.Message}", ToastNotificationVariant.Failure);
            }
        }
```

Dialogs are sequential by design (one at a time, in order); the hashing/extraction of each file starts right after its dialog is accepted and the next dialog waits for it. That is acceptable: picking many files by hand is rare and the previous parallel loop gave no feedback at all.

- [ ] **Step 2: "Editar" command**

`ILibraryViewModel.cs`: add `ReactiveCommand<Unit> EditLocalFileMetadataCommand { get; }` after `RemoveSelectedItemsCommand`.

`LibraryViewModel.cs`, after `RemoveSelectedItemsCommand` is built:

```csharp
        var hasSingleLocalFile = Adapter.SelectedModels
            .ObserveChanged()
            .Select(_ => TryGetSingleSelectedLocalFile(out _))
            .Prepend(false);

        EditLocalFileMetadataCommand = hasSingleLocalFile.ToReactiveCommand<Unit>(
            executeAsync: (_, cancellationToken) => EditLocalFileMetadata(cancellationToken),
            awaitOperation: AwaitOperation.Drop,
            initialCanExecute: false,
            configureAwait: false
        );
```

and the two helpers next to `RemoveSelectedItems`:

```csharp
    private bool TryGetSingleSelectedLocalFile(out LocalFile.ReadOnly localFile)
    {
        localFile = default;
        var ids = GetSelectedIds();
        if (ids.Length != 1) return false;
        var candidate = LocalFile.Load(_connection.Db, ids[0]);
        if (!candidate.IsValid()) return false;
        localFile = candidate;
        return true;
    }

    private async ValueTask EditLocalFileMetadata(CancellationToken cancellationToken)
    {
        if (!TryGetSingleSelectedLocalFile(out var localFile)) return;
        var libraryItem = localFile.AsLibraryFile().AsLibraryItem();
        var initial = new LocalFileMetadata(
            Name: libraryItem.Name,
            Version: LocalFile.Version.TryGetValue(localFile, out var version) ? version : null,
            Source: LocalFile.Source.TryGetValue(localFile, out var source) ? source : null,
            PageUri: LocalFile.PageUri.TryGetValue(localFile, out var uri) ? uri : null);
        var overlay = new LocalFileMetadataOverlayViewModel(
            title: "Editar archivo local",
            acceptText: "Guardar",
            originPath: localFile.OriginalPath,
            initial: initial);
        var result = await _serviceProvider.GetRequiredService<IOverlayController>().EnqueueAndWait(overlay);
        if (!result.Confirmed) return;
        await _libraryService.UpdateLocalFileMetadata(localFile.LocalFileId, result.Metadata);
    }
```

`using NexusMods.Abstractions.Library;` and `using NexusMods.Sdk.Library;` are probably already imported; add what the compiler asks for. If `ObserveChanged()` is not available on `ObservableHashSet`, use `ObserveCountChanged()` like the sibling commands and accept that swapping one single selection for another single selection does not re-evaluate until the count changes; in that case also keep the `TryGetSingleSelectedLocalFile` guard inside `EditLocalFileMetadata` (already there) so the command is harmless when stale.

- [ ] **Step 3: Button in the toolbar**

`LibraryView.axaml`, right after the `RemoveModButton` element (inside the same `ItemsControl`):

```xml
                            <controls:StandardButton x:Name="EditLocalFileButton"
                                                     Text="Editar"
                                                     Type="Tertiary"
                                                     Size="Toolbar"
                                                     Fill="None"
                                                     ShowIcon="Left"
                                                     LeftIcon="{x:Static icons:IconValues.Pencil}"
                                                     ToolTip.Tip="Editar nombre, versión, fuente y página de un archivo agregado a mano" />
```

`LibraryView.axaml.cs`, next to the `RemoveModButton` binding (line 64):

```csharp
                this.BindCommand(ViewModel, vm => vm.EditLocalFileMetadataCommand, view => view.EditLocalFileButton)
                    .DisposeWith(disposables);
```

- [ ] **Step 4: Version column and mod page for local files**

`LocalFileDataProvider.SetupLibraryItemModel`: replace the `AddViewModPageActionComponent(itemModel, isEnabled: false)` line and add the version component:

```csharp
        if (LocalFile.Version.TryGetValue(localFile, out var version))
            itemModel.Add(LibraryColumns.ItemVersion.CurrentVersionComponentKey, new VersionComponent(value: version));
        LibraryDataProviderHelper.AddViewModPageActionComponent(itemModel, isEnabled: LocalFile.PageUri.TryGetValue(localFile, out _));
```

Add `using NexusMods.App.UI.Controls;` if `VersionComponent` is not resolved (it lives in `NexusMods.App.UI.Controls.TreeDataGrid`; check `NexusModsDataProvider.cs` usings and copy the one it uses).

`LibraryViewModel.cs`: change the message and its two consumers.

```csharp
public readonly record struct ViewModPageMessage(OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId, LocalFileId> Id);
```

`GetModPageIdOneOfType` (static local in the adapter, line ~1095) returns the three-way `OneOf`; before the final `throw` add:

```csharp
            var localFile = LocalFile.Load(db, entityId);
            if (localFile.IsValid())
                return OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId, LocalFileId>.FromT2(localFile.LocalFileId);
```

and change the two existing `FromT0`/`FromT1` to the three-type `OneOf`. `ViewChangelogMessage` shares this helper: if it is declared with the two-type `OneOf`, give it the three-type one too and make `HandleViewChangelogMessage` ignore the `LocalFileId` case (`_ => ValueTask.CompletedTask`); changelogs stay Nexus-only and the component is disabled for local files anyway.

`HandleViewModPageMessage`:

```csharp
    private ValueTask HandleViewModPageMessage(ViewModPageMessage viewModPageMessage, CancellationToken cancellationToken)
    {
        return viewModPageMessage.Id.Match(
            modPageMetadataId => OpenModPage(modPageMetadataId),
            libraryItemId => OpenModPage(new NexusModsLibraryItem.ReadOnly(_connection.Db, libraryItemId).ModPageMetadataId),
            localFileId =>
            {
                var local = LocalFile.Load(_connection.Db, localFileId);
                if (LocalFile.PageUri.TryGetValue(local, out var uri))
                    _serviceProvider.GetRequiredService<IOSInterop>().OpenUri(uri);
                return ValueTask.CompletedTask;
            }
        );
    }
```

- [ ] **Step 5: Build and run the UI test project**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | grep -E "error|warn" | grep -v NU19 | head; echo BUILD-DONE && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.UI.Tests --filter "RequiresNetworking!=True&FlakeyTest!=True" 2>&1 | tail -4`
Expected: clean build, UI tests PASS (the project exists under `tests/`; if its name differs, `ls tests | grep -i ui`).

- [ ] **Step 6: Manual check in the app**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet run --project src/NexusMods.App/NexusMods.App.csproj`
Check, with a throwaway zip in `~/Descargas`:
1. Biblioteca → "Add from drive" → pick the zip → dialog appears prefilled → Enter → the row shows the name and version; `~/.local/share/tModManager/Downloads/` has the copy; the original is still in `~/Descargas`.
2. Select the row → "Editar" enabled → change the URL → Guardar → "Ver página del mod" icon opens the browser.
3. Select a Nexus row → "Editar" disabled.
Record the result in the commit message body ("probado en la app: ...").

- [ ] **Step 7: Commit**

```bash
git add src/NexusMods.App.UI/Pages/LibraryPage src/NexusMods.App.UI/Pages/LocalFileDataProvider.cs
git commit -m "feat(ui): metadata dialog on add, Editar button, version and page for local files"
```

---

### Task 9: Docs, full verification, PR

**Files:**
- Modify: `TODO.md` (status header line 9, "Archivos locales fuera de Descargas" line 116, pieces table line 175)
- Modify: `CLAUDE.md` (Fork-Specific Features: one line on local files)
- Modify: `docs/superpowers/specs/2026-10-10-local-mods-first-class-design.md` (status line)

- [ ] **Step 1: Update `TODO.md`**

- Line 9 ("Próximo"): replace the piece-3-or-4 sentence with: `Pieza 4 (`IIntrinsicFile`, primer uso: `UserSettings.json`) de "Piezas genéricas para el segundo juego". Las piezas 2 (prefix, PR #73) y 3 (mods locales, PR #NN) están hechas; la 3 queda por probar en la app con un archivo real.`
- Line 116: change `- [ ] **Archivos locales fuera de Descargas:**` to `- [x]` and append ` Hecho (pieza 3, PR #NN): `AddLocalFileJob` copia a Descargas vía `DownloadsFolder.PlaceAsync`, deduplica por hash, y `LocalFileBackfill` repara los viejos al arrancar.`
- Line 175 (pieces table, row 3): `| 3 | Mods locales de primera clase (hecha 2026-10-10, PR #NN; `LocalFile.Version/Source/PageUri`, copia a Descargas, diálogo "Editar"; spec en `docs/superpowers/specs/2026-10-10-local-mods-first-class-design.md`) | archivos agregados a mano | mods de mod.io/GitHub/foros | KOTOR |`
- Under "Para probar en Linux": add `- [ ] **Pieza 3 en la app:** agregar un zip desde `~/Descargas`, ver la copia en `tModManager/Downloads`, editar metadata, "Ver página del mod"; arrancar con un `LocalFile` viejo y ver el backfill en el log` (skip if Task 8 step 6 was done on this machine; then write the result in "Pruebas reales" instead).

Use the real PR number once it exists (Step 5); write `#NN` now and amend in Step 5.

- [ ] **Step 2: Update `CLAUDE.md`**

In "Fork-Specific Features", add item 11:

```
11. **Local mods first-class** (`AddLocalFileJob.cs`, `LocalFileBackfill.cs`, `LocalFileMetadataOverlay`): a file added by hand is copied into `tModManager/Downloads` (`DownloadsFolder.PlaceAsync`, never moved), deduped by hash, and gets `LibraryFile.DownloadPath` like a download. `LocalFile` carries optional `Version`, `Source` (free text) and `PageUri`; the library shows the version and opens the page, and "Editar" changes them. Old local files without `DownloadPath` are copied once at startup.
```

- [ ] **Step 3: Spec status line and the one deviation**

In §4 of the spec replace `Menú contextual de `LibraryView`: ítem **"Editar metadata"**, visible y habilitado solo cuando` with `Botón **"Editar"** en la barra de la biblioteca (la página no tiene menú contextual; todas las acciones por selección son botones), habilitado solo cuando`. Then change the spec's `Estado:` to `implementado en la rama feat/local-mods-first-class (PR #NN), prueba en la app pendiente` (or `probado en la app el 2026-10-DD` if Task 8 step 6 ran).

- [ ] **Step 4: Full verification**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true 2>&1 | grep -cE "warning (CS|CA|AVLN|IL)" ; echo "^ must be 0"`
Then `./dev.sh` option 4 (full suite without network/flakey, one project at a time). Expected: every project green. Paste the per-project summary lines into the PR description. If a project fails, fix it in this task before continuing; do not open the PR red.

- [ ] **Step 5: Commit docs, push, open the PR**

```bash
git add TODO.md CLAUDE.md docs/superpowers/specs/2026-10-10-local-mods-first-class-design.md
git commit -m "docs: piece 3 (local mods first-class) done"
git push -u origin feat/local-mods-first-class
gh pr create --title "feat: local mods as first-class library items (piece 3)" --body-file /tmp/claude-1000/-home-tatoh-Repos-tModManager/407fb78a-dedf-4863-9c2d-1f7196dd5e3d/scratchpad/pr-body.md
```

Write `pr-body.md` first: what changed (one bullet per task), how it was verified (dev.sh option 4 output, manual app check if done), what is out of scope (spec's "Fuera de alcance"). No AI attribution footer. After `gh pr create` prints the number, replace `#NN` in `TODO.md` and the spec, commit `docs: PR number`, push. Do not merge: the user merges on explicit order.
