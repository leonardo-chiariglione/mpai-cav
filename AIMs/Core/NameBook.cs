using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using AIF.SharedStorage;

namespace Mpai.Core;

// THE NAMES OF THE OBJECTS AND SCENES OF A MODULE (the author, 2026/10/26; storage 2026/11/08).
//
// A User, and everything that talks to the Module for the User, identifies an Object or a Scene ONLY by the name the User gave it: the value of BasicAudioObjectID,
// AudioObjectID, AudioSceneDescriptorsID, BasicSpeechObjectID, SpeechObjectID, SpeechSceneDescriptorsID ... is "violin". That name IS the ID of the stored instance, and the
// instance is kept at <Data Type>#<Port>/<ID>@<version> (ModuleStore): there is no table between a name and a key.
//
//   - A name is unique within what the Module keeps of one kind (Audio Object, Speech Object, Audio Scene, Speech Scene, Multimodal Scene). A second thing given a name that
//     is taken is "name 2", then "name 3" - the next free number; it is not refused.
//   - A thing with no name is not created: the editor says that a name is needed. Names are never made up for the User.
public enum NamedKind { AudioObject, SpeechObject, AudioScene, SpeechScene, MultimodalScene }

// Raised where a thing would be created with no name; the AIM reports it.
public sealed class NameNeededException(string what) : InvalidOperationException($"a name is needed for {what}") { }

public static class NameBook
{
    public static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    // The name, or the first free one: "violin", "violin 2", "violin 3" ... Free among the kinds given (a Basic and a Full Audio Object share one name space).
    public static string Free(ModuleStore store, string? wanted, params StoredKind[] kinds)
    {
        var name = Clean(wanted) ?? throw new NameNeededException(kinds[0].ToString());
        if (!kinds.Any(k => store.Holds(k, name))) return name;
        for (var n = 2; ; n++)
            if (!kinds.Any(k => store.Holds(k, $"{name} {n}"))) return $"{name} {n}";
    }
}

// WHAT A LIST OPERATION ASKS FOR AND GIVES (the author, 2026/10/26): the Kinds of the request are the kinds of what the Module keeps; each entry is a name, the Kind, and -
// for something made of others - the names of its members. Never a storage key.
public static class ListKinds
{
    public const string AudioObject = "Audio Object", BasicAudioObject = "Basic Audio Object", SpeechObject = "Speech Object", BasicSpeechObject = "Basic Speech Object",
                        AudioScene = "Audio Scene", SpeechScene = "Speech Scene", MultimodalScene = "Multimodal Scene", AcousticProfile = "Acoustic Profile", Listener = "Listener", ClosedSpace = "Closed Space", All = "All";

    public static readonly string[] Every = [AudioObject, BasicAudioObject, SpeechObject, BasicSpeechObject, AudioScene, SpeechScene, MultimodalScene, AcousticProfile, Listener, ClosedSpace, All];

    public static bool Known(string? kind) => kind is not null && Every.Contains(kind);

    // The Editing AIM that keeps (and so lists) the Kind: a List is addressed to its command Port.
    public static string ListedBy(string kind) => kind switch
    {
        AudioObject or BasicAudioObject   => "Audio Object Editing",
        SpeechObject or BasicSpeechObject => "Speech Object Editing",
        AudioScene or AcousticProfile or Listener or ClosedSpace => "Audio Scene Editing",
        SpeechScene                       => "Speech Scene Editing",
        MultimodalScene                   => "Multimodal Scene Editing",
        _                                 => ""
    };

    // Does a request for this Kind ask for entries of that Kind?
    public static bool Asks(string requested, string kind) => requested == kind || (requested == All && kind != AcousticProfile && kind != Listener && kind != ClosedSpace);      // the Acoustic Profile, the Listener and the Closed Space are asked for by their Kind, not as part of All
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

    // WHAT THE LIST SAYS OF THE ACOUSTIC PROFILE of the open Scene (Kind "Acoustic Profile"): the Profile as the Scene holds it; absent where the Scene has none.
    public AcousticProfile?    AcousticProfile { get; init; }

    // WHAT THE LIST SAYS OF THE LISTENER and of the CLOSED SPACE of a Scene (M3282 UCM-2): the Point of View the Scene is heard from, and its room.
    public PointOfView?        UserPoV         { get; init; }
    public System.Text.Json.Nodes.JsonObject? ClosedSpace { get; init; }
}

// A member of a Scene as the Scene holds it: its name, its position and orientation in the Scene, when it sounds there and its loudness there.
public sealed class ListedMember
{
    public string           ObjectID { get; init; } = "";
    public SpatialAttitude? PoV      { get; init; }
    public SimpleTime?      Time     { get; init; }
    public double?          Level    { get; init; }
}

// THE DELETION A USER COMMAND ASKS FOR, as every Editing AIM does it for the kind it keeps: each name the command gives is deleted if this AIM keeps a thing of that name; one it keeps
// none of is reported failed (the Editing AIM that keeps it reports it done, and the answers of all are one response); one that something holds is not deleted and says by what.
public enum DeletionOutcome { Deleted, NotKept, Held }

public static class Deletions
{
    // The names that went, for the AIM to let go of what it had open. delete does it for a name of the kind: the outcome and, for Held, by what.
    public static HashSet<string> Apply(UserCommandRQ data, Func<string, (DeletionOutcome Outcome, string? Reason)> delete, string what, CommandReport report)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (data.DeletedObjects is not { Objects.Count: > 0 } deleted) return gone;
        foreach (var entry in deleted.Objects)
        {
            if (entry.ObjectID is null && entry.AcousticProfileID is not null) continue;      // a kept Acoustic Profile is deleted by the AIM that lists them (Audio Scene Editing)
            var name = entry.ObjectID?.ObjectID ?? entry.ObjectID?.BasicAudioObject?.BasicAudioObjectID;
            if (NameBook.Clean(name) is not { } clean) { report.Failed("DeletedObjects", null, "the name of what is to be deleted is needed"); continue; }
            var result = delete(clean);
            switch (result.Outcome)
            {
                case DeletionOutcome.Deleted: gone.Add(clean); report.Done("DeletedObjects", clean); break;
                case DeletionOutcome.Held: report.Failed("DeletedObjects", clean, result.Reason ?? "something holds it"); break;
                default: report.Failed("DeletedObjects", clean, $"the Module keeps no {what} of that name"); break;
            }
        }
        return gone;
    }
}
