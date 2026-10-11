using System.Text;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>An intrinsic file whose entries the CLI can validate and list, and whose state the status check can read.</summary>
public interface ISettingsIntrinsicFile : IIntrinsicFile
{
    bool TryValidate(string key, string value, out string? error);
    /// <summary>Enabled entries for this file, one per key: External Changes win, otherwise the newest entity.</summary>
    IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout);
    /// <summary>True when every owned key already has its value in <paramref name="diskText"/> (always true with nothing owned).</summary>
    bool IsUpToDate(string diskText, Loadout.ReadOnly loadout);
}

/// <summary>
/// A settings file the game rewrites, where the loadout owns some keys. Ingest reads the disk, puts
/// the owned keys' values in, turns a game-made change to an owned key into an External Change entry
/// (which wins), and returns the bytes to write when something has to change. The base kept in
/// <see cref="IntrinsicFileState"/> is what the file looks like after the apply, so a deleted or
/// broken file is regenerated from it. The game defines only the format.
/// </summary>
public abstract class ASettingsIntrinsicFile<TDoc>(GamePath path) : ISettingsIntrinsicFile
{
    public GamePath Path { get; } = path;

    /// <summary>Text to document. Empty text must yield a valid empty document.</summary>
    protected abstract TDoc Parse(string text);
    protected abstract string Serialize(TDoc document);
    protected abstract bool TryGet(TDoc document, string key, out string value);
    /// <summary>Throws InvalidOperationException naming the key when the value is not valid for this format.</summary>
    protected abstract void Set(TDoc document, string key, string value);
    /// <summary>
    /// Comparable form of a literal, for "is this the same value" checks only ("5.0" and "5" for a JSON
    /// number). What goes into the file and into an External Change entry is always the literal itself.
    /// </summary>
    protected virtual string NormalizeLiteral(string literal) => literal;

    public bool TryValidate(string key, string value, out string? error)
    {
        try
        {
            Set(Parse(string.Empty), key, value);
            error = null;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            error = e.Message;
            return false;
        }
    }

    public IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var winners = new Dictionary<string, IntrinsicFileEntry.ReadOnly>();
        var winnerIsOverride = new HashSet<string>();
        foreach (var entry in IntrinsicFileEntry.FindByFile(db, Path))
        {
            var item = entry.AsLoadoutItem();
            if (item.LoadoutId != loadout.LoadoutId || !item.IsEnabled()) continue;
            var isOverride = IsExternalChange(entry);
            if (winners.TryGetValue(entry.Key, out var current))
            {
                var currentIsOverride = winnerIsOverride.Contains(entry.Key);
                if (currentIsOverride && !isOverride) continue;
                if (currentIsOverride == isOverride && current.Id.Value > entry.Id.Value) continue;
            }
            winners[entry.Key] = entry;
            if (isOverride) winnerIsOverride.Add(entry.Key); else winnerIsOverride.Remove(entry.Key);
        }
        return winners;
    }

    public bool IsUpToDate(string diskText, Loadout.ReadOnly loadout)
    {
        var wanted = WinningValues(loadout);
        if (wanted.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(diskText)) return false;
        TDoc disk;
        try { disk = Parse(diskText); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
        return wanted.All(kv => TryGet(disk, kv.Key, out var onDisk) && NormalizeLiteral(onDisk) == kv.Value);
    }

    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var state = FindState(loadout);
        var text = Render(state.IsValid() ? state.BaseContent : string.Empty, WinningEntries(loadout).ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        var bytes = Encoding.UTF8.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var diskText = await reader.ReadToEndAsync();

        var state = FindState(loadout);
        var entries = WinningEntries(loadout);
        var wanted = entries.ToDictionary(kv => kv.Key, kv => Normalize(kv.Key, kv.Value.Value));
        var missing = string.IsNullOrWhiteSpace(diskText);
        // No file, nothing ingested before and nothing owned: nothing to do, and no reason to invent a
        // file the game has not created yet.
        if (missing && !state.IsValid() && wanted.Count == 0) return null;

        TDoc disk;
        string renderBase;
        bool repair;
        if (missing)
        {
            // Deleted by the game or the user: regenerate from the last base (only when something is owned, below).
            renderBase = state.IsValid() ? state.BaseContent : string.Empty;
            disk = Parse(renderBase);
            repair = true;
        }
        else
        {
            try
            {
                disk = Parse(diskText);
                renderBase = diskText;
                repair = false;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Half-written or hand-broken. With a last good base, repair from it; with none, leave it alone.
                if (!state.IsValid()) return null;
                renderBase = state.BaseContent;
                disk = Parse(renderBase);
                repair = true;
            }
        }

        // Nothing owned: the file is the game's and the user's. Remember it, never rewrite or regenerate it.
        if (wanted.Count == 0)
        {
            if (!repair) UpsertBase(tx, state, loadout, diskText);
            return null;
        }

        // Owned keys the game changed become External Changes with the game's value. "Changed by the
        // game" means the disk differs from our value AND from the value of the previous base (what the
        // file looked like after the last apply): on first sight there is no base and our entry simply
        // applies; after that, a key only counts as changed when the game moved it away from what we left.
        var previousBase = state.IsValid() ? Parse(state.BaseContent) : default;
        // The file gets the entry's own literal ("7.0"); `wanted` is only the normalized form for comparing
        var effective = entries.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        var mustWrite = repair;
        LoadoutOverridesGroupId? overrides = null;
        foreach (var (key, entry) in entries)
        {
            var want = wanted[key];
            var found = TryGet(disk, key, out var onDisk);
            if (found && NormalizeLiteral(onDisk) == want) continue;
            var gameChanged = found && state.IsValid() && !(TryGet(previousBase!, key, out var before) && NormalizeLiteral(before) == NormalizeLiteral(onDisk));
            if (!gameChanged)
            {
                mustWrite = true;
                continue;
            }
            effective[key] = onDisk;
            if (IsExternalChange(entry))
            {
                tx.Add(entry.Id, IntrinsicFileEntry.Value, onDisk);
                continue;
            }
            overrides ??= LoadoutOverrides.GetOrCreate(tx, loadout);
            _ = new IntrinsicFileEntry.New(tx, out var id)
            {
                File = Path, Key = key, Value = onDisk,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = LoadoutItemGroupId.From(overrides.Value.Value) },
            };
        }

        if (!mustWrite)
        {
            UpsertBase(tx, state, loadout, diskText);
            return null;
        }
        var rendered = Render(renderBase, effective);
        UpsertBase(tx, state, loadout, rendered);
        return new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(rendered));
    }

    /// <summary>Values the loadout wants in comparable form (written through the format, then normalized).</summary>
    private Dictionary<string, string> WinningValues(Loadout.ReadOnly loadout) =>
        WinningEntries(loadout).ToDictionary(kv => kv.Key, kv => Normalize(kv.Key, kv.Value.Value));

    private string Normalize(string key, string value)
    {
        try
        {
            var doc = Parse(string.Empty);
            Set(doc, key, value);
            return NormalizeLiteral(TryGet(doc, key, out var written) ? written : value);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return NormalizeLiteral(value);
        }
    }

    private static bool IsExternalChange(IntrinsicFileEntry.ReadOnly entry) =>
        LoadoutItem.Parent.TryGetValue(entry.AsLoadoutItem(), out var parent) && LoadoutOverridesGroup.Load(entry.Db, parent).IsValid();

    private string Render(string baseText, IReadOnlyDictionary<string, string> values)
    {
        var doc = Parse(baseText);
        foreach (var (key, value) in values) Set(doc, key, value);
        return Serialize(doc);
    }

    private void UpsertBase(ITransaction tx, IntrinsicFileState.ReadOnly state, Loadout.ReadOnly loadout, string text)
    {
        if (state.IsValid())
        {
            if (state.BaseContent != text) tx.Add(state.Id, IntrinsicFileState.BaseContent, text);
            return;
        }
        _ = new IntrinsicFileState.New(tx) { LoadoutId = loadout.LoadoutId, File = Path, BaseContent = text };
    }

    private IntrinsicFileState.ReadOnly FindState(Loadout.ReadOnly loadout) =>
        IntrinsicFileState.FindByLoadout(loadout.Db, loadout.LoadoutId).FirstOrDefault(s => s.File == Path);
}
