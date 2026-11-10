using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using AIF.SharedStorage;

namespace Mpai.Core;

// A STORED KIND: a Data Type at a Port of the Module (the author, 2026/11/08: "always write data type and port number"). The Data Type and the Port are the L3's: the Port number is 1
// where the L3 gives none.
public sealed record StoredKind(string DataType, int Port)
{
    public string Prefix => $"{DataType}#{Port}/";
    public override string ToString() => $"{DataType}#{Port}";
}

// The kinds the editors of Audio Scene Management keep. Nothing here is a Port or a Data Type of its own: each is read from the L3s (AOE Output EditedAudioObject, ASE Output
// AudioSceneDescriptors, SOE Outputs EditedSpeechObject 1 and Speech 2, SSE Output SpeechSceneDescriptors, MSE Output MultimodalScene) and from the author's answer for what the L3s
// do not carry (the Basic Audio Object, the Acoustic Profile).
public static class Kinds
{
    public static readonly StoredKind AudioObject = new("OSD-AUO-V1.5", 1);
    public static readonly StoredKind BasicAudioObject = new("OSD-BAO-V1.5", 1);
    public static readonly StoredKind AudioScene = new("OSD-ASD-V1.5", 1);
    public static readonly StoredKind SpeechObject = new("OSD-SPO-V1.5", 1);
    public static readonly StoredKind BasicSpeechObject = new("OSD-BSO-V1.5", 2);
    public static readonly StoredKind SpeechScene = new("OSD-SSD-V1.5", 1);
    public static readonly StoredKind MultimodalScene = new("OSD-MSD-V1.5", 1);
    public static readonly StoredKind AcousticProfile = new("CAE-ACP-V1.0", 1);

    // What the Module offers to other Modules: the Data Types of its Output Ports that carry an Object or a Scene.
    public static readonly StoredKind[] Offered = [AudioObject, AudioScene, SpeechObject, SpeechScene, MultimodalScene];
}

// WHERE THE EDITORS KEEP WHAT THEY EDIT (the author, 2026/11/08).
//
//   - Every instance is kept at   <Data Type>#<Port>/<the Object's own ID>@<version>   in the Module's PRIVATE Storage (M3219 3.1), which the AIMs of the Module share. The ID is the
//     one inside the instance; the version is 1, 2, 3 ...: every edit is a new version, the highest is in force. No table, no counter: what is in force, the version, what holds an
//     Object, are all found in what is stored.
//   - What the Module OFFERS to other Modules - the Objects and Scenes of its Output Ports - is also put in the SHARED Storage, as the resolved instance in force, at
//     <Module name>/<Data Type>#<Port>/<ID>  (the Module name as the L3 gives it).
//
// A datum another AIM wrote is not overwritten or deleted by this one (M3219): each kind has one AIM that writes it, the others read.
public sealed class ModuleStore
{
    private readonly IRuledStorage store;
    private readonly ISharedStorage? shared;
    private readonly string[] readers;

    public string Module { get; }

    public ModuleStore(IRuledStorage? modulePrivate, string module, ISharedStorage? shared, string aim = "an AIM")
    {
        store = modulePrivate ?? throw new InvalidOperationException($"{aim} keeps what it edits in the Private Storage of its Module, and the Module has none.");
        Module = module;
        this.shared = shared;
        readers = [module];
    }

    // ---- the keys ----

    public static string Key(StoredKind kind, string id, int version) => $"{kind.Prefix}{id}@{version}";

    public string SharedKey(StoredKind kind, string id) => $"{Module}/{kind.Prefix}{id}";

    // The ID and the version of a key of the kind; null when it is not one.
    public static (string Id, int Version)? Parse(StoredKind kind, string key)
    {
        if (!key.StartsWith(kind.Prefix, StringComparison.Ordinal)) return null;
        var rest = key[kind.Prefix.Length..];
        var at = rest.LastIndexOf('@');
        if (at <= 0 || !int.TryParse(rest.AsSpan(at + 1), out var version) || version < 1) return null;
        return (rest[..at], version);
    }

    // ---- reading ----

    private IReadOnlyList<string> List(string prefix) => store.MPAI_AIFM_RuledStorage_List(null, prefix);

    // The versions kept of an ID, in order.
    public IReadOnlyList<int> Versions(StoredKind kind, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return [];
        var prefix = $"{kind.Prefix}{id}@";
        return List(prefix).Select(k => Parse(kind, k)).Where(p => p is { } v && v.Id == id).Select(p => p!.Value.Version).OrderBy(v => v).ToList();
    }

    // The version in force: 0 when the Module keeps none.
    public int Latest(StoredKind kind, string? id) => Versions(kind, id) is { Count: > 0 } v ? v[^1] : 0;

    public bool Holds(StoredKind kind, string? id) => Latest(kind, id) > 0;

    public byte[]? Get(StoredKind kind, string? id, int? version = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var n = version ?? Latest(kind, id);
        if (n < 1) return null;
        return store.MPAI_AIFM_RuledStorage_Get(Key(kind, id, n), out var data) == StorageOutcome.OK ? data : null;
    }

    // Every ID kept of the kind, with the version in force, in the order of the IDs.
    public IReadOnlyList<(string Id, int Version)> All(StoredKind kind) =>
        List(kind.Prefix).Select(k => Parse(kind, k)).Where(p => p is not null).Select(p => p!.Value)
            .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => (g.Key, g.Max(p => p.Version))).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

    // ---- writing ----

    // One new version of the ID: the next number. The version written is returned. The undo of the command that makes it is recorded.
    public int Write(StoredKind kind, string id, byte[] data)
    {
        var n = Latest(kind, id) + 1;
        Put(kind, id, n, data);
        AddStep(() => Latest(kind, id) == n, () => { Delete(kind, id, n - 1); });        // undone: the version written is taken away (the versions above the one before it)
        Changed?.Invoke(kind, id);
        return n;
    }

    // The version as it is, written at that number (an Object edited in place, or an earlier one put back); no undo is recorded.
    public void Put(StoredKind kind, string id, int version, byte[] data)
    {
        var outcome = store.MPAI_AIFM_RuledStorage_Put(Key(kind, id, version), data, RuledStore.Default, readers);
        if (outcome != StorageOutcome.OK) throw new UnauthorizedAccessException($"{kind}/{id}@{version} may not be written by this AIM: {outcome}.");
        Changed?.Invoke(kind, id);
    }

    // The versions above the number are removed (0: all).
    public void Delete(StoredKind kind, string id, int above = 0)
    {
        foreach (var v in Versions(kind, id).Where(v => v > above).ToList())
        {
            var outcome = store.MPAI_AIFM_RuledStorage_Delete(Key(kind, id, v));
            if (outcome == StorageOutcome.NotAuthorised) throw new UnauthorizedAccessException($"{kind}/{id}@{v} was written by another AIM of the Module: only that AIM may delete it.");
        }
        Changed?.Invoke(kind, id);
    }

    // What an ID is given a new name: the versions it had are the new ID's too - the same content under the new ID, the numbers go on - and the old ID has none. Undone, the old
    // ID's versions are written back and the new ID's removed.
    public int Move(StoredKind kind, string from, string to, byte[] renamed)
    {
        var old = Versions(kind, from).Select(v => (v, Get(kind, from, v)!)).ToList();
        var n = old.Count == 0 ? 1 : old[^1].v + 1;
        Put(kind, to, n, renamed);
        Delete(kind, from);
        AddStep(() => Latest(kind, to) == n, () =>
        {
            Delete(kind, to);
            foreach (var (v, data) in old) Put(kind, from, v, data);
        });
        return n;
    }

    // ---- undo ----

    private readonly List<(Func<bool> Unchanged, Action Undo)> steps = [];

    private void AddStep(Func<bool> unchanged, Action undo) => steps.Add((unchanged, undo));

    public void ForgetSteps() => steps.Clear();

    // What the command wrote, to be undone: nothing changed since, and the way back (the last first).
    public (Func<bool> Unchanged, Action Undo)? TakeSteps()
    {
        if (steps.Count == 0) return null;
        var taken = steps.ToList();
        steps.Clear();
        return (() => taken.All(s => s.Unchanged()), () => { for (var i = taken.Count - 1; i >= 0; i--) taken[i].Undo(); });
    }

    // ---- what the Module offers ----

    public event Action<StoredKind, string>? Changed;

    public void Offer(StoredKind kind, string id, byte[]? resolved)
    {
        if (shared is null) return;      // the Module has no Shared Storage: nothing is offered
        var key = SharedKey(kind, id);
        if (resolved is null) shared.MPAI_AIFM_SharedStorage_Delete(key);
        else shared.MPAI_AIFM_SharedStorage_Put(key, resolved);
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
