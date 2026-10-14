using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Generic;

/// <summary>
/// Checks the prefix against the game's <see cref="IGameData.WinePrefixRequirements"/>. Games that declare
/// requirements add it to their <c>DiagnosticEmitters</c>.
/// </summary>
public class WinePrefixRequirementsEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installation = loadout.InstallationInstance;
        var requirements = installation.Game.WinePrefixRequirements;
        if (requirements is null) yield break;

        await foreach (var diagnostic in Diagnose(installation, requirements, cancellationToken))
            yield return diagnostic;
    }

    public static async IAsyncEnumerable<Diagnostic> Diagnose(GameInstallation installation, WinePrefixRequirements requirements, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var game = installation.Game.DisplayName;

        if (requirements.DllOverrides.Length > 0)
        {
            var dllOverridesInstructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(installation, requirements.DllOverrides.ToArray(), cancellationToken: cancellationToken);
            if (dllOverridesInstructions is not null) yield return WinePrefixDiagnostics.CreateRequiredWineDllOverrides(game, dllOverridesInstructions);
        }

        if (requirements.WinetricksPackages.Length > 0)
        {
            var winetricksInstructions = await WineDiagnosticHelper.GetWinetricksInstructions(installation, requirements.WinetricksPackages.ToImmutableHashSet(), cancellationToken: cancellationToken);
            if (winetricksInstructions is not null) yield return WinePrefixDiagnostics.CreateRequiredWinetricksPackages(game, winetricksInstructions);
        }
    }
}
