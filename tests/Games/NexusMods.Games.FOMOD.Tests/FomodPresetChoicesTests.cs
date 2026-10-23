using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.FOMOD.Tests;

/// <summary>
/// Collections install FOMODs with the choices from <c>collection.json</c>; those choices must not stick to the
/// installer and answer the next FOMOD instead of the guided installer window.
/// </summary>
public class FomodPresetChoicesTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<FomodPresetChoicesTests, Cyberpunk2077Game>(outputHelper)
{
    private readonly PickLastOption _guidedInstaller = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddSingleton<IGuidedInstaller>(_guidedInstaller)
            .AddRedEngineGames()
            .AddUniversalGameLocator<Cyberpunk2077Game>(new Version("1.6.1"));
    }

    [Fact]
    public async Task PresetChoices_DoNotAnswerTheNextInstall()
    {
        var path = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("TestCasesPacked/SimpleInstaller.fomod");
        var archive = await RegisterLocalArchive(path);
        var installer = FomodXmlInstaller.Create(ServiceProvider, new GamePath(LocationId.Game, ""));
        var loadout = await CreateLoadout();

        FomodOption[] preset =
        [
            new() { name = "Step 1", groups = [new() { name = "Group 1", choices = [new() { name = "Step 1 Group 1 Plugin 1", idx = 0 }] }] },
            new() { name = "Step 2", groups = [new() { name = "Group 1", choices = [new() { name = "Step 2 Group 1 Plugin 1", idx = 0 }] }] },
        ];

        var fromCollection = await Install(installer, loadout, archive, preset);
        FileNames(fromCollection).Should().BeEquivalentTo("g1p1f1.out.esp", "g2p1f1.out.esp");
        _guidedInstaller.Requests.Should().Be(0);

        var byHand = await Install(installer, loadout, archive, options: null);
        _guidedInstaller.Requests.Should().Be(2, "both steps have to reach the guided installer");
        FileNames(byHand).Should().BeEquivalentTo("g1p2f1.out.esp", "g2p2f1.out.esp");
    }

    private async Task<LoadoutItemGroup.ReadOnly> Install(FomodXmlInstaller installer, Loadout.ReadOnly loadout, LibraryArchive.ReadOnly archive, FomodOption[]? options)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId)
            {
                Name = archive.AsLibraryFile().AsLibraryItem().Name,
                LoadoutId = loadout,
            },
        };

        var result = await installer.ExecuteAsync(archive, group, tx, loadout, options, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var commit = await tx.Commit();
        return commit.Remap(group);
    }

    private static IEnumerable<string> FileNames(LoadoutItemGroup.ReadOnly group) =>
        group.Children.Select(x => ((GamePath)x.ToLoadoutItemWithTargetPath().TargetPath).FileName.ToString());

    /// <summary>
    /// Stands in for the window: picks the last option of every group and counts how often it was asked.
    /// </summary>
    private sealed class PickLastOption : IGuidedInstaller
    {
        public int Requests { get; private set; }

        public void Dispose() { }

        public void SetupInstaller(string name) { }

        public void CleanupInstaller() { }

        public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
        {
            Requests++;
            var picks = installationStep.Groups.Select(group => new SelectedOption(group.Id, group.Options[^1].Id)).ToArray();
            return Task.FromResult(new UserChoice(new UserChoice.GoToNextStep(picks)));
        }
    }
}
