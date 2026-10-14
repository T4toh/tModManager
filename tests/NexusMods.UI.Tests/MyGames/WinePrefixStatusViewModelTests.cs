using FluentAssertions;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;

namespace NexusMods.UI.Tests.MyGames;

public class WinePrefixStatusViewModelTests
{
    [Fact]
    public void MissingProtontricksIsNotAnIssueWithoutPackages()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(isProtontricksInstalled: false, packages: [], hasCorrectDllOverrides: true)
            .Should().BeFalse();
    }

    [Fact]
    public void MissingProtontricksIsAnIssueWhenTheGameNeedsPackages()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(isProtontricksInstalled: false, packages: [new WinetricksPackageStatus("vcrun2022", IsInstalled: true)], hasCorrectDllOverrides: true)
            .Should().BeTrue();
    }

    [Fact]
    public void MissingPackageOrWrongOverridesAreIssues()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(true, [new WinetricksPackageStatus("vcrun2022", IsInstalled: false)], true).Should().BeTrue();
        WinePrefixStatusViewModel.ComputeHasIssues(true, [], hasCorrectDllOverrides: false).Should().BeTrue();
        WinePrefixStatusViewModel.ComputeHasIssues(true, [new WinetricksPackageStatus("vcrun2022", IsInstalled: true)], true).Should().BeFalse();
    }
}
