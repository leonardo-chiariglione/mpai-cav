using System;
using System.Collections.Generic;
using System.Linq;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;   // Summary (MMC-SUM-V2.5)

namespace Mpai.Mmc.Scm;

// MMC-SCM-V2.5 - Summary Creation Module, as an AIF IAimProcessor.
//
// Adds what an entity said to the Summary: one line per utterance - who said it
// (the Entity ID), how (the Personal Status: its Emotion), what (the Refined Text) -
// after the Summary it was given back edited by Entity Dialogue Processing. The
// Meaning (Text Descriptors) is taken but not yet used: a first implementation keeps
// the record of the meeting, and leaves condensing it to the dialogue (EDP, asked by
// a participant) rather than to a rule.
//
// IT KEEPS NOTHING. The Summary goes round, in the Virtual Meeting Secretary, from
// Summary Creation to Entity Dialogue Processing and back as the Edited Summary;
// before the first utterance there is none, and the Summary begins with it.
public sealed class ScmAimProcessor : IAimProcessor
{
    private readonly string _instanceId;
    private readonly string _entityIdPort;     // the ID of the entity that spoke: an ID (string), declared
    private readonly string _textPort;         // OSD-BTO: the Refined Text
    private readonly string _psPort;           // MMC-EPS
    private readonly string _editedPort;       // MMC-SUM in: the Edited Summary
    private readonly string _summaryPort;      // MMC-SUM out

    public ScmAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId   = instanceId;
        _entityIdPort = ports.Input("string");
        _textPort     = ports.Input("OSD-BTO-V1.5");
        _psPort       = ports.Input("MMC-EPS-V2.5");
        _editedPort   = ports.Input("MMC-SUM-V2.5");
        _summaryPort  = ports.Output("MMC-SUM-V2.5");
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        T? Read<T>(string port) where T : class =>
            message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? MpaiJson.FromJson<T>(json) : null;

        var record = Read<Summary>(_editedPort)?.Text() ?? "";
        var said = Read<BasicTextObject>(_textPort)?.GetText().Replace('\n', ' ').Trim() ?? "";

        // NO WORDS IS NOTHING SAID: the Summary goes on as it came in.
        if (said.Length > 0)
        {
            var who = message.Ports.TryGetValue(_entityIdPort, out var named) && !string.IsNullOrWhiteSpace(named) ? System.Text.Json.JsonSerializer.Deserialize<string>(named) : null;
            var how = EmotionOf(Read<EntityPersonalStatus>(_psPort));
            var line = $"{(string.IsNullOrWhiteSpace(who) ? "A participant" : who)}{(how is null ? "" : $" ({how})")}: {said}";
            record = record.Length == 0 ? line : record + "\n" + line;
        }

        return System.Threading.Tasks.Task.FromResult(new Message
        {
            MessageId = message.MessageId,
            MessageType = message.MessageType,
            Ports = new Dictionary<string, string> { [_summaryPort] = MpaiJson.ToJson(Summary.Of(record)) }
        });
    }

    // The Emotion of the Personal Status, from whichever modality carries one; none
    // where it is neutral, which goes without saying.
    private static string? EmotionOf(EntityPersonalStatus? ps)
    {
        var emotion = ps?.TextPersonalStatus?.TextEmotion ?? ps?.SpeechPersonalStatus?.SpeechEmotion
                      ?? ps?.FacePersonalStatus?.FaceEmotion ?? ps?.BodyPersonalStatus?.BodyEmotion;
        var word = emotion?.GeneralAdjectival ?? emotion?.Category?.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(word) || word == "neutral" ? null : word;
    }
}
