using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Pages;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.UI.Tests;

public class LocalFileDataProviderTests : AUiTest
{
    public LocalFileDataProviderTests(IServiceProvider provider) : base(provider) { }

    [Fact]
    public async Task EditingTheMetadata_RefreshesTheRow()
    {
        var provider = Provider.GetServices<ILibraryDataProvider>().OfType<LocalFileDataProvider>().Single();
        var filter = new LibraryFilter(LoadoutId.From(EntityId.From(0xDEADBEEF)), Provider.GetServices<IGameData>().First());

        using var tx = Connection.BeginTransaction();
        var lib = new LibraryFile.New(tx, out var id)
        {
            FileName = (RelativePath)"Mod.zip",
            Hash = NexusMods.Hashing.xxHash3.Hash.From(7),
            Size = Size.From(1),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "Antes" },
        };
        var local = new LocalFile.New(tx, id) { LibraryFile = lib, OriginalPath = "/tmp/Mod.zip" };
        var result = await tx.Commit();
        var localId = result[local.Id];

        using var _ = provider.ObserveLibraryItems(filter).Bind(out var rows).Subscribe();
        await Eventually(() => rows.Should().ContainSingle());
        NameOf(rows.Single()).Should().Be("Antes");
        rows.Single().GetOptional<VersionComponent>(LibraryColumns.ItemVersion.CurrentVersionComponentKey).HasValue.Should().BeFalse();

        using var edit = Connection.BeginTransaction();
        edit.Add(localId, LibraryItem.Name, "Después");
        edit.Add(localId, LocalFile.Version, "2.0");
        await edit.Commit();

        await Eventually(() => NameOf(rows.Single()).Should().Be("Después"));
        rows.Single().Get<VersionComponent>(LibraryColumns.ItemVersion.CurrentVersionComponentKey).Value.Value.Should().Be("2.0");
    }

    private static string NameOf(CompositeItemModel<EntityId> row) => row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value;
}
