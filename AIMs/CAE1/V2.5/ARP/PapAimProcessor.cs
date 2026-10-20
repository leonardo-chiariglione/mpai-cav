using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Arp;

// CAE-PAP-V2.5, Packaging for Audio Preservation: the Preservation Audio File (and, if there are any, the Restored Audio Files, the Editing List, the Irregularity File, the
// Irregularity Images and the Preservation Audio-Visual File) in; the Preservation Master Files and the Access Copy Files out.
//
// THE MASTER keeps the digitised audio as it is: the bytes of the Preservation Audio File and of the Audio-Visual File pass through untouched (whatever their format, 24 bit and
// 96 kHz included: nothing is decoded), with the Irregularity File, the Irregularity Images and the Editing List that document them, and a manifest of the name, size and SHA-256 of
// every file, so that anyone can check later that nothing has changed. It holds nothing restored: it is the recording without any restoration.
// THE ACCESS COPY is the Preservation Audio File with each restored section put in the place of the section it restores (the restored section is brought to the sampling frequency
// of the file: a section read at twice the speed it was written at lasts twice as long). With nothing restored it is a copy of the Preservation Audio File.
public sealed class PapAimProcessor(string instanceId, string audioKey, string restoredKey, string listKey, string irregularityKey, string imagesKey, string avKey, string accessKey, string masterKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    private static string? Get(Message m, string key) => m.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // The AIM runs whenever a Message arrives: what comes at each input is kept, the package is made of what is held, and made again only when what is held has changed (the Irregularity
    // File and the Editing List may come more than once, as the analyses settle). It gives nothing until it has the Preservation Audio File.
    private readonly Dictionary<string, string> _held = new();
    private string _last = "";

    public Task<Message> ProcessAsync(Message incoming)
    {
        foreach (var key in new[] { audioKey, restoredKey, listKey, irregularityKey, imagesKey, avKey })
            if (incoming.Ports.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) _held[key] = v;
        var message = new Message { MessageId = incoming.MessageId, Ports = new Dictionary<string, string>(_held) };
        if (Get(message, audioKey) is not string audioJson) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header });
        var held = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", _held.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "\u0002" + k.Value)))));
        if (held == _last) return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header });      // what is held has not changed
        _last = held;
        try
        {
            var originals = AudioObject.Files(audioJson, "BasicAudioObjectData");
            if (originals.Count != 1) throw new FormatException($"The Preservation Audio File is one audio file: it holds {originals.Count}.");
            var extension = AudioObject.FileFormatOf(audioJson, "AudioQualifier") ?? (IsRaw(audioJson) ? "pcm" : "wav");
            var audioName = "preservation-audio." + extension;

            var master = new List<(string Name, byte[] Bytes)> { (audioName, originals[0].Bytes) };
            if (Get(message, avKey) is string av)
            {
                var ext = AudioObject.FileFormatOf(av, "BasicAudioVisualDataQualifier") ?? "bin"; var i = 0;
                foreach (var file in AudioObject.Files(av, "BasicAudioVisualObjectData")) master.Add((++i == 1 ? $"preservation-audio-visual.{ext}" : $"preservation-audio-visual-{i}.{ext}", file.Bytes));
            }
            if (Get(message, irregularityKey) is string irregularities) master.Add(("irregularities.json", Encoding.UTF8.GetBytes(irregularities)));
            if (Get(message, imagesKey) is string images)
            {
                var ext = AudioObject.FileFormatOf(images, "VisualQualifier") ?? "img"; var i = 0;
                foreach (var file in AudioObject.Files(images, "BasicVisualObjectData")) master.Add(($"irregularity-image-{++i}.{ext}", file.Bytes));
            }
            if (Get(message, listKey) is string list) master.Add(("editing-list.json", Encoding.UTF8.GetBytes(list)));
            master.Add(("manifest.json", Encoding.UTF8.GetBytes(Manifest(master))));

            var access = Access(audioName, originals[0].Bytes, audioJson, Get(message, restoredKey), Get(message, listKey));
            var masterJson = AudioObject.MakeFiles("pap-master-" + Guid.NewGuid().ToString("N")[..12], master);
            var accessJson = AudioObject.MakeFiles("pap-access-" + Guid.NewGuid().ToString("N")[..12], [access]);
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = masterJson,
                Ports = new Dictionary<string, string> { [accessKey] = accessJson, [masterKey] = masterJson }
            });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(incoming.MessageId, InstanceId, "CAE-PAP-V2.5: " + ex.Message));
        }
    }

    private static bool IsRaw(string audioJson) => JsonNode.Parse(audioJson)?["AudioQualifier"]?["Formats"]?["ContentFormat"]?["RawData"] is not null
                                                  && AudioObject.FileFormatOf(audioJson, "AudioQualifier") is null;

    // The access copy: its name and its bytes.
    private static (string Name, byte[] Bytes) Access(string audioName, byte[] original, string audioJson, string? restoredJson, string? listJson)
    {
        if (restoredJson is null || listJson is null) return ("access-copy" + audioName["preservation-audio".Length..], original);
        var restorations = (JsonNode.Parse(listJson)?["Restorations"] as JsonArray ?? throw new FormatException("The Editing List has no Restorations.")).OfType<JsonObject>()
            .Select(r => (Id: r["RestorationID"]!.GetValue<string>(), Start: Seconds(r["PreservationAudioFileStart"]!.GetValue<string>()), End: Seconds(r["PreservationAudioFileEnd"]!.GetValue<string>())))
            .OrderBy(r => r.Start).ToList();
        var restored = AudioObject.Read(restoredJson).Where(i => i.Id is not null).ToDictionary(i => i.Id!, i => i.Audio);
        var audio = AudioObject.Read(audioJson).Single().Audio;
        var samples = new List<short>(); var at = 0;
        foreach (var r in restorations)
        {
            var from = Math.Clamp((int)Math.Round(r.Start * audio.Rate), 0, audio.Samples.Length); var to = Math.Clamp((int)Math.Round(r.End * audio.Rate), from, audio.Samples.Length);
            if (from < at) throw new FormatException("The restored sections of the Editing List overlap.");
            if (!restored.TryGetValue(r.Id, out var section)) throw new FormatException($"The Editing List names a restoration, {r.Id}, whose restored audio file is not among the Restored Audio Files.");
            samples.AddRange(audio.Samples.AsSpan(at, from - at).ToArray());
            samples.AddRange(section.At(audio.Rate).Samples);
            at = to;
        }
        samples.AddRange(audio.Samples.AsSpan(at).ToArray());
        return ("access-copy.wav", new Audio(samples.ToArray(), audio.Rate).ToWav());
    }

    private static readonly Regex Clock = new(@"^(\d{2}):([0-5]\d):([0-5]\d)\.(\d{3})$");

    private static double Seconds(string clock)
    {
        var m = Clock.Match(clock);
        if (!m.Success) throw new FormatException($"'{clock}' is not a time of the form hh:mm:ss.mmm.");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
               + int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / 1000.0;
    }

    // The manifest of a Preservation Master: each file with its size and its SHA-256, so that a later reader can check that nothing has changed.
    public static string Manifest(IEnumerable<(string Name, byte[] Bytes)> files) =>
        new JsonObject
        {
            ["Header"] = "CAE-PAP-MANIFEST",
            ["Files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["Name"] = f.Name, ["Bytes"] = f.Bytes.Length, ["SHA256"] = Convert.ToHexString(SHA256.HashData(f.Bytes)).ToLowerInvariant() }).ToArray())
        }.ToJsonString();
}
