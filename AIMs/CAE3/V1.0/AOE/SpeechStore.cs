using System;
using System.Text;
using System.Text.Json.Nodes;

using AIF.SharedStorage;
using Mpai.Core;

namespace Mpai.Cae.Aoe;

// SPEECH OBJECTS IN THE MODULE'S SHARED STORAGE (the author, 2026/10/03: Audio Scene
// Management translates speech). A Basic Speech Object is kept as it is, as the JSON
// its schema gives, under its own identifier: a User Command names it by that
// identifier, and a Scene holds it by that identifier. Its translation replaces it
// under the same identifier - there is no Object in two versions, nor two Objects at
// one place - so every Scene holding it then holds the translation, at its place.
public static class SpeechStore
{
    public static void Put(ISharedStorage storage, BasicSpeechObject speech) =>
        storage.Put(speech.BasicSpeechObjectID, Encoding.UTF8.GetBytes(MpaiJson.ToJson(speech)));

    // The Basic Speech Object stored under this identifier, or null when there is
    // none - or what is there is not a Speech Object.
    public static BasicSpeechObject? Get(ISharedStorage storage, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !storage.Exists(id)) return null;
        var json = Encoding.UTF8.GetString(storage.Get(id));
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
