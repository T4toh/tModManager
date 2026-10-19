using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.Collections.Tests;

/// <summary>
/// <see cref="CollectionDownloader.RescanDownloads"/> must link a download that is already on disk, however small
/// (a 447-byte mod was skipped and downloaded again after every reset).
/// </summary>
public class RescanDownloadsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RescanDownloadsTests>(helper)
{
    [Fact]
    public async Task TinyDownloadOnDisk_IsLinked()
    {
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var content = new byte[447];
        Random.Shared.NextBytes(content);
        var file = downloads.Combine("tiny.7z");
        await file.WriteAllBytesAsync(content);

        using var tx = Connection.BeginTransaction();
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(1),
            Slug = CollectionSlug.From("tiny"),
            Name = "Tiny",
            GameId = NexusModsGameId.From(3333),
            AuthorId = tx.TempId(),
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(1),
            RevisionNumber = RevisionNumber.From(1),
            CollectionId = collection.Id,
        };
        _ = new CollectionDownloadExternal.New(tx, out var downloadId)
        {
            CollectionDownload = new CollectionDownload.New(tx, downloadId)
            {
                CollectionRevisionId = revision.Id,
                Name = "tiny",
                IsOptional = false,
                ArrayIndex = 0,
            },
            Md5 = Md5Value.From(MD5.HashData(content)),
            Size = Size.From((ulong)content.Length),
            Uri = new Uri("https://example.com/tiny.7z"),
        };
        var result = await tx.Commit();

        var downloader = ServiceProvider.GetRequiredService<CollectionDownloader>();
        var matched = await downloader.RescanDownloads(CollectionRevisionMetadata.Load(result.Db, result[revision.Id]), CancellationToken.None);

        matched.Should().Be(1);
    }

    [Fact]
    public async Task NexusDownloadOnDisk_IsLinkedOnTheFirstRescan()
    {
        // Seen 2026-10-16 after a reset: every match logged "Entity doesn't have attribute LibraryFile/FileName" because the
        // file just added to the library was read through the db snapshot taken before the rescan. The second rescan worked.
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var content = new byte[1234];
        Random.Shared.NextBytes(content);
        await downloads.Combine("nexus-mod.zip").WriteAllBytesAsync(content);

        using var tx = Connection.BeginTransaction();
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(2),
            Slug = CollectionSlug.From("nexus"),
            Name = "Nexus",
            GameId = NexusModsGameId.From(3333),
            AuthorId = tx.TempId(),
        };
        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(2),
            RevisionNumber = RevisionNumber.From(1),
            CollectionId = collection.Id,
        };
        var modPage = new NexusModsModPageMetadata.New(tx)
        {
            Uid = ModUid.FromUlong(1),
            Name = "Nexus Mod",
            GameDomain = GameDomain.From("cyberpunk2077"),
            UpdatedAt = DateTimeOffset.UtcNow,
            DataUpdatedAt = DateTimeOffset.UtcNow,
        };
        var fileMetadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = FileUid.FromUlong(1),
            Name = "nexus-mod",
            Version = "1.0",
            UploadedAt = DateTimeOffset.UtcNow,
            Size = Size.From((ulong)content.Length),
            ModPageId = modPage.Id,
        };
        _ = new CollectionDownloadNexusMods.New(tx, out var downloadId)
        {
            CollectionDownload = new CollectionDownload.New(tx, downloadId)
            {
                CollectionRevisionId = revision.Id,
                Name = "nexus-mod",
                IsOptional = false,
                ArrayIndex = 0,
            },
            FileUid = FileUid.FromUlong(1),
            ModUid = ModUid.FromUlong(1),
            FileMetadataId = fileMetadata.Id,
        };
        var result = await tx.Commit();

        var downloader = ServiceProvider.GetRequiredService<CollectionDownloader>();
        var matched = await downloader.RescanDownloads(CollectionRevisionMetadata.Load(result.Db, result[revision.Id]), CancellationToken.None);

        matched.Should().Be(1);
        var linked = Connection.Db.Datoms(NexusModsLibraryItem.FileMetadata, result[fileMetadata.Id]).Should().ContainSingle("the file on disk is now a library item linked to the Nexus file").Subject;
        // Seen 2026-10-19: the rescan added the file with AddLocalFile and left it a LocalFile too, so the local-file
        // and the Nexus data providers both listed it and "All" showed twice the mods.
        LocalFile.OriginalPath.TryGetValue(LibraryFile.Load(Connection.Db, linked.E), out _).Should().BeFalse("a file the collection recognises is a Nexus item, not a local file");
    }
}
