using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using AIF.SharedStorage;

namespace Mpai.Core;

// THE NAMES OF THE OBJECTS AND SCENES OF A MODULE (the author, 2026/10/26).
//
// A User, and everything that talks to the Module for the User, identifies an Object or a Scene ONLY by the name the User gave it: the value of BasicAudioObjectID,
// AudioObjectID, AudioSceneDescriptorsID, BasicSpeechObjectID, SpeechObjectID, SpeechSceneDescriptorsID ... is "violin". The Module's Repository keeps its own typed
// storage key for each version of each thing (BAO000001, AUO000002 ...); that key is private to the Module. This book is the table between the two: the name designates
// the version in force (the newest, or the one a revocation put back), and every version keeps the name it had.
//
//   - A name is unique within what the Module keeps of one kind (Audio Object, Speech Object, Audio Scene, Speech Scene, Multimodal Scene). A second thing given a name that
//     is taken is "name 2", then "name 3" - the next free number; it is not refused.
//   - A thing with no name is not created: the editor says that a name is needed. Names are never made up for the User.
//   - The key is never in an output Data Type, in a User Command report or in speech.
public enum NamedKind { AudioObject, SpeechObject, AudioScene, SpeechScene, MultimodalScene }

// Raised where a thing would be created with no name; the AIM reports it.
public sealed class NameNeededException(string what) : InvalidOperationException($"a name is needed for {what}") { }

public sealed class NameBook
{
    private readonly ISharedStorage storage;

    public NameBook(ISharedStorage storage) => this.storage = storage;

    private static string ByName(NamedKind kind, string name) => $"name:{kind}:{name}";
    private static string ByKey(string key) => $"keyname:{key}";

    public static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    // The name, or the first free one: "violin", "violin 2", "violin 3" ...
    public string Free(NamedKind kind, string wanted)
    {
        var name = Clean(wanted) ?? throw new NameNeededException(kind.ToString());
        if (!storage.MPAI_AIFM_SharedStorage_Exists(ByName(kind, name))) return name;
        for (var n = 2; ; n++)
            if (!storage.MPAI_AIFM_SharedStorage_Exists(ByName(kind, $"{name} {n}"))) return $"{name} {n}";
    }

    // A new thing under the key: it has the name wanted if that is free, else the next free one; returned.
    public string Claim(NamedKind kind, string? wanted, string key)
    {
        var name = Free(kind, wanted ?? throw new NameNeededException(kind.ToString()));
        Point(kind, name, key);
        return name;
    }

    // The name now designates this key (a new version of the same thing, or an earlier one put back).
    public void Point(NamedKind kind, string name, string key)
    {
        storage.MPAI_AIFM_SharedStorage_Put(ByName(kind, name), Encoding.UTF8.GetBytes(key));
        storage.MPAI_AIFM_SharedStorage_Put(ByKey(key), Encoding.UTF8.GetBytes(name));
    }

    // The key now in force under the name; null when there is none.
    public string? KeyOf(NamedKind kind, string? name) =>
        Clean(name) is { } n && storage.MPAI_AIFM_SharedStorage_Exists(ByName(kind, n))
            ? Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(ByName(kind, n))) : null;

    // The key under a name of whichever kind has it (audio first); for a name that comes from a Port whose kind the AIM does not know.
    public string? KeyOfAny(string? name)
    {
        foreach (var kind in Enum.GetValues<NamedKind>())
            if (KeyOf(kind, name) is { } key) return key;
        return null;
    }

    // The name of a key, of any of its versions; null when it has none.
    public string? NameOf(string? key) =>
        key is not null && storage.MPAI_AIFM_SharedStorage_Exists(ByKey(key))
            ? Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(ByKey(key))) : null;

    // A key's name where it is to be told to the User: never the key itself.
    public string? Shown(string? key) => key is null ? null : NameOf(key);

    // The name designates nothing from now on (the Object was renamed: its earlier versions keep the name they had).
    public void Release(NamedKind kind, string name) => storage.MPAI_AIFM_SharedStorage_Delete(ByName(kind, name));

    // The version made from another (an edit): the new key remembers the one it comes from, so that the Version of a thing is told even when the name changed.
    public void Link(string newKey, string fromKey) =>
        storage.MPAI_AIFM_SharedStorage_Put($"prevkey:{newKey}", Encoding.UTF8.GetBytes(fromKey));

    // The Version of the thing whose version in force is this key: 1 for the first, one more for each edit it descends from.
    public int Depth(string key)
    {
        var depth = 1;
        for (var at = key; storage.MPAI_AIFM_SharedStorage_Exists($"prevkey:{at}") && depth < 100000; depth++)
            at = Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get($"prevkey:{at}"));
        return depth;
    }

    // The version number of the thing named: how many versions the Module keeps under that name - of the key families given (BAO, AUO ...), as the first is 1.
    public int VersionsOf(string name, params string[] families)
    {
        var count = 0;
        foreach (var k in storage.MPAI_AIFM_SharedStorage_List("keyname:"))
        {
            var key = k["keyname:".Length..];
            if (!families.Any(f => key.StartsWith(f, StringComparison.Ordinal))) continue;
            if (Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(k)) == name) count++;
        }
        return Math.Max(count, 1);
    }

    // DELETION: a thing of the kind, by its name, with all its versions (the chain of the keys that descend from one another). Not deleted if another thing holds it - an Object that is a
    // member of a Scene -: the Scene is deleted first. Returns what was deleted (the keys and the name), so that an AIM that has it open lets go.
    public enum DeletionOutcome { Deleted, NotKept, Held }
    public sealed record Deletion(DeletionOutcome Outcome, IReadOnlyCollection<string> Gone, string? Reason);

    public Deletion Delete(NamedKind kind, string? name)
    {
        var key = KeyOf(kind, name);
        if (key is null || Clean(name) is not { } clean) return new Deletion(DeletionOutcome.NotKept, [], null);
        var keys = new List<string>();
        for (var at = key; !keys.Contains(at); )
        {
            keys.Add(at);
            if (!storage.MPAI_AIFM_SharedStorage_Exists($"prevkey:{at}")) break;
            at = Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get($"prevkey:{at}"));
        }
        var holders = keys.SelectMany(k => storage.MPAI_AIFM_SharedStorage_List($"refby:{k}:").Select(r => r[$"refby:{k}:".Length..])).Where(h => !keys.Contains(h)).Distinct().ToList();
        if (holders.Count > 0)
        {
            var names = holders.Select(h => NameOf(h) ?? "a scene").Distinct().ToList();
            return new Deletion(DeletionOutcome.Held, [], $"it is a member of {string.Join(", ", names)}: delete that first");
        }
        var gone = new List<string> { clean };
        foreach (var k in keys)
        {
            foreach (var to in storage.MPAI_AIFM_SharedStorage_List($"ref:{k}:").ToList())
            {
                storage.MPAI_AIFM_SharedStorage_Delete(to);
                storage.MPAI_AIFM_SharedStorage_Delete($"refby:{to[$"ref:{k}:".Length..]}:{k}");
            }
            storage.MPAI_AIFM_SharedStorage_Delete(k);
            storage.MPAI_AIFM_SharedStorage_Delete(ByKey(k));
            storage.MPAI_AIFM_SharedStorage_Delete($"prevkey:{k}");
            gone.Add(k);
        }
        Release(kind, clean);
        return new Deletion(DeletionOutcome.Deleted, gone, null);
    }

    // Every name of the kind with the key now in force, in the order of the names.
    public IReadOnlyList<(string Name, string Key)> All(NamedKind kind)
    {
        var prefix = $"name:{kind}:";
        return storage.MPAI_AIFM_SharedStorage_List(prefix)
            .Select(k => (Name: k[prefix.Length..], Key: Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(k))))
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
}

// WHAT A LIST OPERATION ASKS FOR AND GIVES (the author, 2026/10/26): the Kinds of the request are the kinds of what the Module keeps; each entry is a name, the Kind, and -
// for something made of others - the names of its members. Never a storage key.
public static class ListKinds
{
    public const string AudioObject = "Audio Object", BasicAudioObject = "Basic Audio Object", SpeechObject = "Speech Object", BasicSpeechObject = "Basic Speech Object",
                        AudioScene = "Audio Scene", SpeechScene = "Speech Scene", MultimodalScene = "Multimodal Scene", All = "All";

    public static readonly string[] Every = [AudioObject, BasicAudioObject, SpeechObject, BasicSpeechObject, AudioScene, SpeechScene, MultimodalScene, All];

    public static bool Known(string? kind) => kind is not null && Every.Contains(kind);

    // The Editing AIM that keeps (and so lists) the Kind: a List is addressed to its command Port.
    public static string ListedBy(string kind) => kind switch
    {
        AudioObject or BasicAudioObject   => "Audio Object Editing",
        SpeechObject or BasicSpeechObject => "Speech Object Editing",
        AudioScene                        => "Audio Scene Editing",
        SpeechScene                       => "Speech Scene Editing",
        MultimodalScene                   => "Multimodal Scene Editing",
        _                                 => ""
    };

    // Does a request for this Kind ask for entries of that Kind?
    public static bool Asks(string requested, string kind) => requested == All || requested == kind;
}

public sealed class ListedObject
{
    public string        ObjectID        { get; init; } = "";
    public string        Kind            { get; init; } = "";
    public List<string>? MemberObjectIDs { get; init; }

    // WHAT THE LIST SAYS OF AN OBJECT (the author, 2026/10/26): its Duration in seconds, the number of children it is made of, and its Version (1 for the first, one more for
    // every edit). Absent where the Kind has none (a Scene has no Duration here; the Duration of an Object that carries no sound is not known).
    public double?       Duration        { get; init; }
    public int?          ChildCount      { get; init; }
    public int?          Version         { get; init; }

    // WHAT THE LIST SAYS OF A SCENE named in the request (ObjectID): the number of its members and, for each, its name and its PoV - the position and orientation it has
    // as provided in the Scene. Never a key; no Object is touched to say it.
    public int?                ObjectCount { get; init; }
    public List<ListedMember>? Members     { get; init; }
}

// A member of a Scene as the Scene holds it: its name, and its position and orientation in the Scene.
public sealed class ListedMember
{
    public string           ObjectID { get; init; } = "";
    public SpatialAttitude? PoV      { get; init; }
}


// THE DELETION A USER COMMAND ASKS FOR, as every Editing AIM does it for the kind it keeps: each name the command gives is deleted if this AIM keeps a thing of that name; one it keeps
// none of is reported failed (the Editing AIM that keeps it reports it done, and the answers of all are one response); one that something holds is not deleted and says by what.
public static class Deletions
{
    // The names and the keys that went, for the AIM to let go of what it had open.
    public static HashSet<string> Apply(UserCommandRQ data, NameBook book, NamedKind kind, string what, CommandReport report)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (data.DeletedObjects is not { Objects.Count: > 0 } deleted) return gone;
        foreach (var entry in deleted.Objects)
        {
            var name = entry.ObjectID?.ObjectID ?? entry.ObjectID?.BasicAudioObject?.BasicAudioObjectID;
            if (NameBook.Clean(name) is null) { report.Failed("DeletedObjects", null, "the name of what is to be deleted is needed"); continue; }
            var result = book.Delete(kind, name);
            switch (result.Outcome)
            {
                case NameBook.DeletionOutcome.Deleted: foreach (var g in result.Gone) gone.Add(g); report.Done("DeletedObjects", name); break;
                case NameBook.DeletionOutcome.Held: report.Failed("DeletedObjects", name, result.Reason ?? "something holds it"); break;
                default: report.Failed("DeletedObjects", name, $"the Module keeps no {what} of that name"); break;
            }
        }
        return gone;
    }
}
