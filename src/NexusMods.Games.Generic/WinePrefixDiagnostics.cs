using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.Generic;

public static partial class WinePrefixDiagnostics
{
    public const string Source = "NexusMods.Games.Generic";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredWineDllOverrides = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Incorrect WINE configuration")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Update the WINE DLL overrides to mod on Linux")
        .WithDetails("""
Modding {Game} on Linux requires you to configure WINE or the game will not launch correctly.

A WINE DLL override is a configuration setting that tells WINE not to use its own built-in version of a specific Windows DLL, but instead to use a native (real) Windows DLL file that you provide.

### How to Resolve
{Instructions}

### Technical Details
Many mods use custom loaders or injectors to hook into the game process that are implemented as wrapper or replacements for system DLLs. These mod-provided DLLs would normally get loaded on Windows when they are found inside the game folder, but on WINE, the default behaviour is different and the internal WINE versions are used by default instead. Configuring WINE DLL OVERRIDEs allows changing that behaviour to instead load the mod provided DLLs.

The current WINE DLL overrides are not set up correctly for modding on Linux.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Instructions")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredWinetricksPackages = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Missing Winetricks packages")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Install required packages to mod on Linux")
        .WithDetails("""
Modding {Game} on Linux requires certain packages to be installed in your WINE prefix.

Winetricks packages are often required for modding because they provide essential components or libraries which allow the app to manage your game correctly.


### How to Resolve
{Instructions}

### Technical Details
The current WINE prefix does not have the required packages installed for modding on Linux.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Instructions")
        )
        .Finish();
}
