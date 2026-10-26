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
}
