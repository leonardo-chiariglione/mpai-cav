using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Soe;

// SPEECH OBJECTS IN THE MODULE'S SHARED STORAGE (the author, 2026/10/03: Audio Scene
// Management translates speech). A Basic Speech Object is kept as it is, as the JSON
// its schema gives, under its own identifier: a User Command names it by that
// identifier, and a Scene holds it by that identifier. Its translation replaces it
// under the same identifier - there is no Object in two versions, nor two Objects at
// one place - so every Scene holding it then holds the translation, at its place.
public static class SpeechStore
{
    // A Basic Speech Object kept under its own identifier - BSO000001 ... when it has
    // none - which is returned.
    public static string Keep(ISharedStorage storage, BasicSpeechObject speech)
    {
        if (string.IsNullOrWhiteSpace(speech.BasicSpeechObjectID))
        {
            const string counter = "_counter:BSO";
            long next = storage.MPAI_AIFM_SharedStorage_Exists(counter) ? long.Parse(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
            storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
            speech = With(speech, id: $"BSO{next:D6}");
        }
        Put(storage, speech);
        return speech.BasicSpeechObjectID;
    }

    // A SPEECH OBJECT (OSD-SPO) IS A COLLECTION OF BASIC SPEECH OBJECTS. One that holds a single Basic Speech Object is that Object, in the form a Port of the
    // Speech Object Data Type asks for: Wrap makes the Speech Object of one, under the Basic Object's own identifier; Alone gives the Basic Object back.
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

    // A Speech Object of more than one Basic Speech Object is kept here: each Basic Speech Object as a new Object, if the storage does not hold it, and the Speech
    // Object, under its own identifier - SPO000001 ... when it has none - holding them by their identifiers. One the storage holds already is that one.
    public static string KeepObject(ISharedStorage storage, SpeechObject speech)
    {
        if (!string.IsNullOrWhiteSpace(speech.SpeechObjectID) && GetObject(storage, speech.SpeechObjectID, resolve: false) is not null) return speech.SpeechObjectID;
        var basics = (speech.BasicSpeechObjects ?? []).Select(e =>
        {
            var b = e.BSObjectIDOrBSObject ?? throw new InvalidOperationException("The Speech Object names a speech it does not hold.");
            var id = Get(storage, b.BasicSpeechObjectID) is not null ? b.BasicSpeechObjectID : Keep(storage, b);
            return new BasicSpeechObjectEntry { BasicSpeechObjectSpaceTime = e.BasicSpeechObjectSpaceTime, BSObjectIDOrBSObject = new BasicSpeechObject { BasicSpeechObjectID = id } };
        }).ToList();
        var subs = (speech.SubSpeechObjects ?? []).Select(e =>
        {
            var s = e.SubSObjectIDOrSubSObject ?? throw new InvalidOperationException("The Speech Object names a Speech Object it does not hold.");
            return new SubSpeechObjectEntry { SubSpeechObjectSpaceTime = e.SubSpeechObjectSpaceTime, SubSObjectIDOrSubSObject = new SpeechObject { SpeechObjectID = KeepObject(storage, s) } };
        }).ToList();
        if (basics.Count + subs.Count == 0) throw new InvalidOperationException("The Speech Object holds no speech.");
        var kept = string.IsNullOrWhiteSpace(speech.SpeechObjectID) ? Next(storage, "SPO") : speech.SpeechObjectID;
        var stored = new SpeechObject
        {
            MInstanceID = speech.MInstanceID, SpeechObjectID = kept, SpeechObjectSpaceTime = speech.SpeechObjectSpaceTime ?? new SpaceTime(), UserPoV = speech.UserPoV,
            BasicSpeechObjectCount = basics.Count, BasicSpeechObjects = basics.Count > 0 ? basics : null,
            SubSpeechObjectCount = subs.Count, SubSpeechObjects = subs.Count > 0 ? subs : null,
            DataXMData = speech.DataXMData, DescrMetadata = speech.DescrMetadata
        };
        storage.MPAI_AIFM_SharedStorage_Put(kept, Encoding.UTF8.GetBytes(MpaiJson.ToJson(stored)));
        return kept;
    }

    // The Speech Object kept under this identifier, with the Objects it holds as the storage now has them (a Basic Speech Object translated in its place is the
    // translation in it); null when there is none - or what is there is not a Speech Object.
    public static SpeechObject? GetObject(ISharedStorage storage, string id, bool resolve = true)
    {
        if (string.IsNullOrWhiteSpace(id) || !storage.MPAI_AIFM_SharedStorage_Exists(id)) return null;
        var json = Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(id));
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

    private static string Next(ISharedStorage storage, string prefix)
    {
        var counter = $"_counter:{prefix}";
        long next = storage.MPAI_AIFM_SharedStorage_Exists(counter) ? long.Parse(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(counter))) + 1 : 1;
        storage.MPAI_AIFM_SharedStorage_Put(counter, Encoding.UTF8.GetBytes(next.ToString()));
        return $"{prefix}{next:D6}";
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

    public static void Put(ISharedStorage storage, BasicSpeechObject speech) =>
        storage.MPAI_AIFM_SharedStorage_Put(speech.BasicSpeechObjectID, Encoding.UTF8.GetBytes(MpaiJson.ToJson(speech)));

    // The Basic Speech Object stored under this identifier, or null when there is
    // none - or what is there is not a Speech Object.
    public static BasicSpeechObject? Get(ISharedStorage storage, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !storage.MPAI_AIFM_SharedStorage_Exists(id)) return null;
        var json = Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(id));
        try { return JsonNode.Parse(json)?["Header"]?.GetValue<string>() == "OSD-BSO-V1.5" ? MpaiJson.FromJson<BasicSpeechObject>(json) : null; }
        catch { return null; }
    }

    // The translation, made to stand where the original stood: the original's
    // identifier and place, the translation's speech, and its Qualifier - which
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
