using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.DataModel.Storage;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.StorageManager;

[JsonName("NexusMods.App.UI.Pages.StorageManager.StorageManagerPageContext")]
public record StorageManagerPageContext : IPageFactoryContext;

[UsedImplicitly]
public class StorageManagerPageFactory(IServiceProvider serviceProvider)
    : APageFactory<IStorageManagerPageViewModel, StorageManagerPageContext>(serviceProvider)
{
    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
    public override PageFactoryId Id => StaticId;

    public override IStorageManagerPageViewModel CreateViewModel(StorageManagerPageContext context)
    {
        return new StorageManagerPageViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(),
            ServiceProvider.GetRequiredService<IStorageAnalyzer>(),
            ServiceProvider.GetRequiredService<IGarbageCollectorRunner>(),
            ServiceProvider
        );
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Storage Manager",
            Icon = IconValues.HardDrive,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new StorageManagerPageContext(),
            },
        };
    }
}
