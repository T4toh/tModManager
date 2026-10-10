using System.Text;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>An intrinsic file whose entries the CLI can validate and list.</summary>
public interface ISettingsIntrinsicFile : IIntrinsicFile
{
    bool TryValidate(string key, string value, out string? error);
    /// <summary>Enabled entries for this file, one per key: External Changes win, otherwise the newest entity.</summary>
    IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout);
}

/// <summary>
/// A settings file the game rewrites, where the loadout owns some keys. Write renders the last
/// ingested base plus the winning entries; Ingest snapshots the disk as the new base, turns a
/// game-made change to an owned key into an External Change entry (which wins), and returns the
/// bytes that must be on disk afterwards. The game defines only the format.
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
        var diskBytes = Encoding.UTF8.GetBytes(diskText);
        var diskHash = diskBytes.xxHash3();

        var state = FindState(loadout);
        var winners = WinningEntries(loadout);
        // No file, nothing ingested before and nothing owned: there is nothing to write and no reason to
        // invent a file the game has not created yet.
        if (diskText.Length == 0 && !state.IsValid() && winners.Count == 0) return null;

        TDoc disk;
        string baseText;
        if (diskText.Length == 0)
        {
            // The game (or the user) deleted the file: regenerate it from the last base, never replace
            // the base with nothing.
            baseText = state.IsValid() ? state.BaseContent : string.Empty;
            disk = Parse(baseText);
            diskHash = Hash.Zero;
        }
        else
        {
            try
            {
                disk = Parse(diskText);
                baseText = diskText;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A half-written or hand-broken file. With a last good base, repair the file from it; with
                // none (first sight), leave the file alone rather than replace what we cannot read.
                if (!state.IsValid()) return null;
                baseText = state.BaseContent;
                disk = Parse(baseText);
                diskHash = Hash.Zero;
            }
        }

        if (state.IsValid())
        {
            if (state.BaseContent != baseText) tx.Add(state.Id, IntrinsicFileState.BaseContent, baseText);
            if (state.IngestedHash != diskHash) tx.Add(state.Id, IntrinsicFileState.IngestedHash, diskHash);
        }
        else
        {
            _ = new IntrinsicFileState.New(tx) { LoadoutId = loadout.LoadoutId, File = Path, BaseContent = baseText, IngestedHash = diskHash };
        }

        // Owned keys the game changed become External Changes with the game's value. "Changed by the
        // game" means the disk differs from our value AND from the value of the previous base: on first
        // sight (no base yet) the disk holds the original and our entry simply applies; after that, a
        // key we wrote only counts as changed when the game moved it away from what the last ingest saw.
        var effective = winners.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        var previousBase = state.IsValid() ? Parse(state.BaseContent) : default;
        LoadoutOverridesGroupId? overrides = null;
        foreach (var (key, entry) in winners)
        {
            if (!TryGet(disk, key, out var onDisk) || onDisk == entry.Value) continue;
            if (!state.IsValid() || (TryGet(previousBase!, key, out var before) && before == onDisk)) continue;
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

        var rendered = Encoding.UTF8.GetBytes(Render(baseText, effective));
        if (rendered.AsSpan().SequenceEqual(diskBytes)) return null;
        return new ReadOnlyMemory<byte>(rendered);
    }

    private static bool IsExternalChange(IntrinsicFileEntry.ReadOnly entry) =>
        LoadoutItem.Parent.TryGetValue(entry.AsLoadoutItem(), out var parent) && LoadoutOverridesGroup.Load(entry.Db, parent).IsValid();

    private string Render(string baseText, IReadOnlyDictionary<string, string> values)
    {
        var doc = Parse(baseText);
        foreach (var (key, value) in values) Set(doc, key, value);
        return Serialize(doc);
    }

    private IntrinsicFileState.ReadOnly FindState(Loadout.ReadOnly loadout) =>
        IntrinsicFileState.FindByLoadout(loadout.Db, loadout.LoadoutId).FirstOrDefault(s => s.File == Path);
}
