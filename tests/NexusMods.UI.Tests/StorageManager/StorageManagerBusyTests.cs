using FluentAssertions;
using NexusMods.Abstractions.GC;
using NexusMods.App.UI.Pages.StorageManager;
using NexusMods.App.UI.Windows;
using NexusMods.DataModel.Storage;
using NSubstitute;
using R3;

namespace NexusMods.UI.Tests.StorageManager;

// AUiTest: the page's tab icon needs the Avalonia platform
public class StorageManagerBusyTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public async Task OtherOperationsAreDisabledWhileOneRuns()
    {
        var gcRunning = new TaskCompletionSource();
        var gcRunner = Substitute.For<IGarbageCollectorRunner>();
        gcRunner.RunAsync().Returns(gcRunning.Task);
        var vm = new StorageManagerPageViewModel(Substitute.For<IWindowManager>(), Substitute.For<IStorageAnalyzer>(), gcRunner, Substitute.For<IServiceProvider>());

        ReactiveCommand<Unit>[] others = [vm.DeepCleanCommand, vm.DeleteDownloadsCommand, vm.DeleteProtonPrefixCommand, vm.RunGarbageCollectionCommand];
        others.Should().AllSatisfy(c => c.CanExecute().Should().BeTrue());

        vm.RunGarbageCollectionCommand.Execute(Unit.Default);
        vm.IsBusy.Should().BeTrue();
        others.Should().AllSatisfy(c => c.CanExecute().Should().BeFalse());

        gcRunning.SetResult();
        for (var i = 0; i < 50 && vm.IsBusy; i++) await Task.Delay(100);
        vm.IsBusy.Should().BeFalse();
        others.Should().AllSatisfy(c => c.CanExecute().Should().BeTrue());
    }
}
