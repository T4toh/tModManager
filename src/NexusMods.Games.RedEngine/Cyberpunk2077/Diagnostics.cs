using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Abstractions.Diagnostics.Values;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Generators.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public static partial class Diagnostics
{
    public const string Source = "NexusMods.Games.RedEngine.Cyberpunk2077";

    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate MissingModWithKnownNexusUri = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' which is not installed")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyName}** is not installed.


### How to Resolve
1. Use the **Essential Mods** page to easily install core frameworks.
2. Download **{DependencyName}** from {NexusModsDependencyUri}
3. Add **{DependencyName}** to the loadout. 

OR 

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}` which indicates that the **{DependencyName}** mod is required.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("DependencyName")
            .AddValue<NamedLink>("NexusModsDependencyUri")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
        )
        .Finish();

    
    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate MissingModWithKnownNexusUriWithStringSegment = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' which is not installed")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyName}** is not installed.


### How to Resolve
1. Use the **Essential Mods** page to easily install core frameworks.
2. Download **{DependencyName}** from {NexusModsDependencyUri}
3. Add **{DependencyName}** to the loadout. 

OR 

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}`. The file has a reference to **{DependencyName}** on line {LineNumber} indicating that the **{DependencyName}** mod is required.

```
{MatchingSegment}
```
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("DependencyName")
            .AddValue<NamedLink>("NexusModsDependencyUri")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
            .AddValue<string>("MatchingSegment")
            .AddValue<int>("LineNumber")
        )
        .Finish();

    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate DisabledGroupDependency = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' but it is disabled")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyGroup}** is not enabled.


### How to Resolve
1. Enable **{DependencyGroup}** in "Installed Mods"

OR

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}` which indicates that the **{DependencyName}** mod is required.

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddDataReference<LoadoutItemGroupReference>("DependencyGroup")
            .AddValue<string>("DependencyName")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
        )
        .Finish();
    
    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate DisabledGroupDependencyWithStringSegment = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Mod Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' but it is disabled")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyGroup}** is not enabled.


### How to Resolve
1. Enable **{DependencyGroup}** in "Installed Mods"

OR

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}`. The file has a reference to **{DependencyName}** on line {LineNumber} indicating that the **{DependencyName}** mod is required.

```
{MatchingSegment}
```
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddDataReference<LoadoutItemGroupReference>("DependencyGroup")
            .AddValue<string>("DependencyName")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
            .AddValue<string>("MatchingSegment")
            .AddValue<int>("LineNumber")
        )
        .Finish();
    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingProtontricksForRedMod = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Protontricks is not installed")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Protontricks is required to use REDmods but is not present.")
        .WithDetails("""
Protontricks is not installed but is required to use the REDmod DLC when playing Cyberpunk 2077 on Linux.

### How to Resolve
1. Install Protontricks by following the instructions in the {ProtontricksUri}.
2. Restart the app.

OR

1. Remove any mods requiring REDmod from "Installed Mods".

### Technical Details
The OS did not return any information when querying for Protontricks installation information. This indicates that Protontricks is not installed.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<NamedLink>("ProtontricksUri")
        )
        .Finish();
    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingRedModDependency = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("REDmod DLC is not installed")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The official REDmod DLC is required for {ModCount} mod(s) but is not installed.")
        .WithDetails("""
The official REDmod DLC is a mod loader required to use some mods for Cyberpunk 2077. The loadout contains {ModCount} mod(s) that require REDmod to work, but it is not installed.

### How to Resolve
1. Install the REDmod DLC - View in {RedmodLink} or the {GenericLink}.
2. Apply a loadout with REDmod installed or restart the app.

OR

1. Remove any mods requring REDmod from Installed Mods.
2. Remove any subfolders of `{RedModFolder}` from External Changes.

### Technical Details 
Each subfolder in the `{RedModFolder}` represents an installed mod. If any folders are present, REDmod is required. 

After finding {ModCount} mod(s), the REDmod DLC was checked for installation by verifying that `{RedModEXE}` exists.

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<NamedLink>("RedmodLink")
            .AddValue<NamedLink>("GenericLink")
            .AddValue<int>("ModCount")
            .AddValue<string>("RedModFolder")
            .AddValue<string>("RedModEXE")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RedundantFolderDetected = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("Redundant folder structure detected")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Mod '{ModName}' may be installed incorrectly due to a redundant folder.")
        .WithDetails("""
The mod **{ModName}** appears to have a redundant folder structure (e.g., `Cyberpunk 2077/Cyberpunk 2077/...`). This usually happens when a mod is compressed with an extra top-level folder.

### How to Resolve
1. Re-install the mod ensuring the folder structure matches the game's directory (e.g., `bin`, `r6`, `archive` should be at the root of the mod).

### Technical Details
Found a file or folder `{RedundantPath}` that indicates a redundant level. Common game folders like `bin`, `r6`, or `red4ext` should be at the root of the mod's structure.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("ModName")
            .AddValue<string>("RedundantPath")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RedscriptCompilationFailed = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("Redscript compilation issue")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Redscript may have failed to compile.")
        .WithDetails("""
Redscript files were detected in `r6/scripts`, but the script cache in `r6/cache` has not been updated. This usually indicates that Redscript failed to compile your scripts when the game started.

### How to Resolve
1. Check the Redscript logs in `{RedscriptLogPath}` for errors.
2. Ensure all required dependencies for your scripts are installed.
3. Try deleting the `r6/cache/final.pc` file (it will be recreated) and launching the game again.

### Technical Details
The folder `r6/scripts` contains `.reds` files, but the compiler output in `r6/cache` is missing or outdated.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("RedscriptLogPath")
        )
        .Finish();
}
