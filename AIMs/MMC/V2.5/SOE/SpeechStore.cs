using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Mmc.Soe;

// SPEECH OBJECTS IN THE MODULE'S SHARED STORAGE (the author, 2026/10/03: Audio Scene
// Management translates speech). A Basic Speech Object is kept as it is, as the JSON
// its schema gives, with the NAME the User gave it as its identifier ("hello"): a User
// Command names it by that name, and a Scene holds it by that name. The Module's own
// storage key (BSO000001 ...) is private to it; the Module's NameBook is the table between
// the two (the author, 2026/10/26). A name is unique among the Speech Objects the Module
// keeps: a second Object given a name that is taken is "hello 2". An Object with no name
// is not kept. Its translation takes its place, named as the original with "-trans" added
// (the author, 2026/11/10); the original is not kept.
public static class SpeechStore
{
    public static bool Holds(ModuleStore storage, string? name) =>
        NameBook.Clean(name) is { } n && (storage.Holds(Kinds.BasicSpeechObject, n) || storage.Holds(Kinds.SpeechObject, n));

    // A Basic Speech Object kept under its name - or the next free one if the name is taken: the name it has is returned. One with no name is not kept.
    public static string Keep(ModuleStore storage, BasicSpeechObject speech)
    {
        if (NameBook.Clean(speech.BasicSpeechObjectID) is not { } wanted) throw new NameNeededException("the Basic Speech Object");
        var name = NameBook.Free(storage, wanted, Kinds.BasicSpeechObject, Kinds.SpeechObject);
        speech = With(speech, id: name);
        storage.Put(Kinds.BasicSpeechObject, name, 1, Encoding.UTF8.GetBytes(MpaiJson.ToJson(speech)));
        return name;
    }

    // Is this arriving Basic Speech Object the speech kept under its name? One that carries no speech, or the same speech, is.
    public static bool IsTheSpeech(ModuleStore storage, BasicSpeechObject arriving)
    {
        if (arriving.Data is not { Length: > 0 }) return true;
        return Get(storage, arriving.BasicSpeechObjectID) is { } held && held.Data.AsSpan().SequenceEqual(arriving.Data);
    }

    // A SPEECH OBJECT (OSD-SPO) IS A COLLECTION OF BASIC SPEECH OBJECTS. One that holds a single Basic Speech Object is that Object, in the form a Port of the
    // Speech Object Data Type asks for: Wrap makes the Speech Object of one, under the Basic Object's own name; Alone gives the Basic Object back.
    public static SpeechObject Wrap(BasicSpeechObject basic) => new()
    {
        MInstanceID = basic.MInstanceID, SpeechObjectID = basic.BasicSpeechObjectID,
        SpeechObjectSpaceTime = basic.BasicSpeechObjectSpaceTime ?? new SpaceTime(), UserPoV = basic.UserPoV,
        BasicSpeechObjectCount = 1, BasicSpeechObjects = [new BasicSpeechObjectEntry { BSObjectIDOrBSObject = basic }]
    };

    public static BasicSpeechObject? Alone(SpeechObject speech) =>
        speech.BasicSpeechObjects is { Count: 1 } one && speech.SubSpeechObjects is not { Count: > 0 } ? one[0].BSObjectIDOrBSObject : null;

    // What arrives on a Speech Object Port: a Speech Object - or, from an AIM that gives its Basic Speech Object as it is (Text and Speech Translation lists both),
    // a Basic Speech Object, which is the Speech Object of one.
    public static SpeechObject Parse(string json) =>
        JsonNode.Parse(json)?["Header"]?.GetValue<string>() == "OSD-BSO-V1.5" ? Wrap(MpaiJson.FromJson<BasicSpeechObject>(json)) : MpaiJson.FromJson<SpeechObject>(json);

    // THE BASIC SPEECH OBJECTS OF A SPEECH OBJECT (the author, 2026/11/08: everything in ASM is Full, a Basic Object is the case of one part): the Basic Speech Objects the name stands for, at any
    // depth (its sub-Objects too), each with where it stands in the frame the Object is placed in - the placement of the Object composed with its own. A Basic Speech Object is its own part.
    public static IReadOnlyList<(string Name, SpaceTime? Placement)> Leaves(ModuleStore storage, string? name, SpaceTime? placement)
    {
        if (name is null) return [];
        if (Get(storage, name) is not null) return [(name, placement)];
        if (GetObject(storage, name, resolve: false) is not { } speech) return [];
        var leaves = new List<(string Name, SpaceTime? Placement)>();
        foreach (var e in speech.BasicSpeechObjects ?? [])
            if (e.BSObjectIDOrBSObject?.BasicSpeechObjectID is { } child && Get(storage, child) is not null)
                leaves.Add((child, Placements.Within(placement, Placements.Within(speech.SpeechObjectSpaceTime, e.BasicSpeechObjectSpaceTime))));
        foreach (var e in speech.SubSpeechObjects ?? [])
            if (e.SubSObjectIDOrSubSObject?.SpeechObjectID is { } sub)
                leaves.AddRange(Leaves(storage, sub, Placements.Within(placement, Placements.Within(speech.SpeechObjectSpaceTime, e.SubSpeechObjectSpaceTime))));
        return leaves;
    }

    // A Speech Object of more than one Basic Speech Object is kept here: each Basic Speech Object as a new Object, if the storage does not hold it, and the Speech
    // Object, under its own name, holding them by their names. One the storage holds already is that one. The name it has is returned.
    public static string KeepObject(ModuleStore storage, SpeechObject speech)
    {
        if (NameBook.Clean(speech.SpeechObjectID) is not { } wanted) throw new NameNeededException("the Speech Object");
        if (GetObject(storage, wanted, resolve: false) is not null) return wanted;
        var basics = (speech.BasicSpeechObjects ?? []).Select(e =>
        {
            var b = e.BSObjectIDOrBSObject ?? throw new InvalidOperationException("The Speech Object names a speech it does not hold.");
            var name = Get(storage, b.BasicSpeechObjectID) is not null && IsTheSpeech(storage, b) ? b.BasicSpeechObjectID : Keep(storage, b);
            return new BasicSpeechObjectEntry { BasicSpeechObjectSpaceTime = e.BasicSpeechObjectSpaceTime, BSObjectIDOrBSObject = new BasicSpeechObject { BasicSpeechObjectID = name } };
        }).ToList();
        var subs = (speech.SubSpeechObjects ?? []).Select(e =>
        {
            var s = e.SubSObjectIDOrSubSObject ?? throw new InvalidOperationException("The Speech Object names a Speech Object it does not hold.");
            return new SubSpeechObjectEntry { SubSpeechObjectSpaceTime = e.SubSpeechObjectSpaceTime, SubSObjectIDOrSubSObject = new SpeechObject { SpeechObjectID = KeepObject(storage, s) } };
        }).ToList();
        if (basics.Count + subs.Count == 0) throw new InvalidOperationException("The Speech Object holds no speech.");
        var kept = NameBook.Free(storage, wanted, Kinds.BasicSpeechObject, Kinds.SpeechObject);
        var stored = new SpeechObject
        {
            MInstanceID = speech.MInstanceID, SpeechObjectID = kept, SpeechObjectSpaceTime = speech.SpeechObjectSpaceTime ?? new SpaceTime(), UserPoV = speech.UserPoV,
            BasicSpeechObjectCount = basics.Count, BasicSpeechObjects = basics.Count > 0 ? basics : null,
            SubSpeechObjectCount = subs.Count, SubSpeechObjects = subs.Count > 0 ? subs : null,
            DataXMData = speech.DataXMData, DescrMetadata = speech.DescrMetadata
        };
        storage.Put(Kinds.SpeechObject, kept, 1, Encoding.UTF8.GetBytes(MpaiJson.ToJson(stored)));
        return kept;
    }

    // The Speech Object kept under this name, with the Objects it holds as the storage now has them (a Basic Speech Object translated in its place is the
    // translation in it); null when there is none - or what is there is not a Speech Object.
    public static SpeechObject? GetObject(ModuleStore storage, string? name, bool resolve = true)
    {
        if (storage.Get(Kinds.SpeechObject, NameBook.Clean(name)) is not { } bytes) return null;
        var json = Encoding.UTF8.GetString(bytes);
        SpeechObject? speech;
        try { speech = JsonNode.Parse(json)?["Header"]?.GetValue<string>() == "OSD-SPO-V1.5" ? MpaiJson.FromJson<SpeechObject>(json) : null; }
        catch { return null; }
        if (speech is null || !resolve) return speech;
        var basics = (speech.BasicSpeechObjects ?? []).Select(e => new BasicSpeechObjectEntry
        {
            BasicSpeechObjectSpaceTime = e.BasicSpeechObjectSpaceTime,
            BSObjectIDOrBSObject = e.BSObjectIDOrBSObject is { } b ? Get(storage, b.BasicSpeechObjectID) ?? b : null
        }).ToList();
        var subs = (speech.SubSpeechObjects ?? []).Select(e => new SubSpeechObjectEntry
        {
            SubSpeechObjectSpaceTime = e.SubSpeechObjectSpaceTime,
            SubSObjectIDOrSubSObject = e.SubSObjectIDOrSubSObject is { } s ? GetObject(storage, s.SpeechObjectID) ?? s : null
        }).ToList();
        return new SpeechObject
        {
            MInstanceID = speech.MInstanceID, SpeechObjectID = speech.SpeechObjectID, SpeechObjectSpaceTime = speech.SpeechObjectSpaceTime, UserPoV = speech.UserPoV,
            BasicSpeechObjectCount = basics.Count, BasicSpeechObjects = basics.Count > 0 ? basics : null,
            SubSpeechObjectCount = subs.Count, SubSpeechObjects = subs.Count > 0 ? subs : null,
            DataXMData = speech.DataXMData, DescrMetadata = speech.DescrMetadata
        };
    }

    // The same Basic Speech Object with some of its fields changed.
    public static BasicSpeechObject With(BasicSpeechObject s, string? id = null, PointOfView? userPoV = null,
                                         SpaceTime? spaceTime = null, SpeechQualifier? qualifier = null) => new()
    {
        MInstanceID = s.MInstanceID, UEnvironmentID = s.UEnvironmentID,
        BasicSpeechObjectID = id ?? s.BasicSpeechObjectID, BasicSpeechObjectTime = s.BasicSpeechObjectTime,
        BasicSpeechObjectSpaceTime = spaceTime ?? s.BasicSpeechObjectSpaceTime,
        Data = s.Data, SpeechQualifier = qualifier ?? s.SpeechQualifier, UserPoV = userPoV ?? s.UserPoV,
        DataXMData = s.DataXMData, DescrMetadata = s.DescrMetadata
    };

    // Puts a Basic Speech Object the Module keeps back where it is, under its name: a Speech Object is edited in place.
    public static void Put(ModuleStore storage, BasicSpeechObject speech)
    {
        if (NameBook.Clean(speech.BasicSpeechObjectID) is not { } id || !storage.Holds(Kinds.BasicSpeechObject, id)) throw new InvalidOperationException("The Module keeps no Speech Object of that name.");
        storage.Put(Kinds.BasicSpeechObject, id, 1, Encoding.UTF8.GetBytes(MpaiJson.ToJson(speech)));
    }

    // The Basic Speech Object stored under this name, or null when there is
    // none - or what is there is not a Speech Object.
    public static BasicSpeechObject? Get(ModuleStore storage, string? name)
    {
        if (storage.Get(Kinds.BasicSpeechObject, NameBook.Clean(name)) is not { } bytes) return null;
        var json = Encoding.UTF8.GetString(bytes);
        try { return JsonNode.Parse(json)?["Header"]?.GetValue<string>() == "OSD-BSO-V1.5" ? MpaiJson.FromJson<BasicSpeechObject>(json) : null; }
        catch { return null; }
    }

    // THE LIST (the author, 2026/10/26): the Speech Objects and Basic Speech Objects the Module keeps, by name; a Speech Object with the names of those it holds.
    public static IReadOnlyList<ListedObject> Listing(ModuleStore storage, string requested, string? named = null)
    {
        var found = new List<ListedObject>();
        var all = storage.All(Kinds.BasicSpeechObject).Select(e => (e.Id, e.Version, Basic: true))
            .Concat(storage.All(Kinds.SpeechObject).Select(e => (e.Id, e.Version, Basic: false)))
            .OrderBy(e => e.Id, StringComparer.Ordinal);
        foreach (var (name, version, basic) in all)
        {
            if (named is not null && name != named) continue;
            if (!ListKinds.Asks(requested, basic ? ListKinds.BasicSpeechObject : ListKinds.SpeechObject)) continue;
            List<string>? members = null;
            if (!basic && GetObject(storage, name, resolve: false) is { } speech)
                members = (speech.BasicSpeechObjects ?? []).Select(e => e.BSObjectIDOrBSObject?.BasicSpeechObjectID)
                    .Concat((speech.SubSpeechObjects ?? []).Select(e => e.SubSObjectIDOrSubSObject?.SpeechObjectID))
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            // What the List says of an Object (the author, 2026/10/26): its Duration (seconds, of its speech, or for a Speech Object the end of the member that lasts longest),
            // the number of members, its Version.
            double? duration = null; var children = 0;
            if (basic) duration = Get(storage, name) is { } one ? SoundLength.Seconds(one.Data) : null;
            else if (GetObject(storage, name, resolve: false) is { } whole)
            {
                children = (whole.BasicSpeechObjects?.Count ?? 0) + (whole.SubSpeechObjects?.Count ?? 0);
                duration = DurationOf(storage, whole);
            }
            found.Add(new ListedObject
            {
                ObjectID = name, Kind = basic ? ListKinds.BasicSpeechObject : ListKinds.SpeechObject, MemberObjectIDs = members,
                Duration = duration, ChildCount = children, Version = version
            });
        }
        return found;
    }

    private static double? DurationOf(ModuleStore storage, SpeechObject whole)
    {
        var ends = new List<double>();
        foreach (var e in whole.BasicSpeechObjects ?? [])
            if (Get(storage, e.BSObjectIDOrBSObject?.BasicSpeechObjectID) is { } b && SoundLength.Seconds(b.Data) is { } d) ends.Add(SoundLength.StartOf(e.BasicSpeechObjectSpaceTime) + d);
        foreach (var e in whole.SubSpeechObjects ?? [])
            if (GetObject(storage, e.SubSObjectIDOrSubSObject?.SpeechObjectID, resolve: false) is { } s && DurationOf(storage, s) is { } d) ends.Add(SoundLength.StartOf(e.SubSpeechObjectSpaceTime) + d);
        return ends.Count > 0 ? Math.Round(ends.Max(), 6) : null;
    }

    // The translation, made to stand where the original stood: the original's
    // name and place, the translation's speech, and its Qualifier - which
    // states the format the speech is in - with the target language the command's
    // Speech Qualifier states.
    public static BasicSpeechObject Replacing(BasicSpeechObject original, BasicSpeechObject translation, SpeechQualifier? target)
    {
        var qualifier = translation.SpeechQualifier;
        var language = target?.Attributes?.Metadata?.Language;
        if (qualifier is not null && language is not null)
        {
            var node = JsonNode.Parse(MpaiJson.ToJson(qualifier))!.AsObject();
            var attributes = node["Attributes"] as JsonObject ?? new JsonObject();
            var metadata = attributes["Metadata"] as JsonObject ?? new JsonObject();
            metadata["Language"] = JsonNode.Parse(MpaiJson.ToJson(language));
            attributes["Metadata"] = metadata;
            node["Attributes"] = attributes;
            qualifier = MpaiJson.FromJson<SpeechQualifier>(node.ToJsonString());
        }
        return new BasicSpeechObject
        {
            MInstanceID                = original.MInstanceID,
            UEnvironmentID             = original.UEnvironmentID,
            BasicSpeechObjectID        = original.BasicSpeechObjectID,
            BasicSpeechObjectTime      = translation.BasicSpeechObjectTime ?? original.BasicSpeechObjectTime,
            BasicSpeechObjectSpaceTime = original.BasicSpeechObjectSpaceTime,
            Data                       = translation.Data,
            SpeechQualifier            = qualifier,
            UserPoV                    = original.UserPoV,
            DescrMetadata              = original.DescrMetadata
        };
    }
}
