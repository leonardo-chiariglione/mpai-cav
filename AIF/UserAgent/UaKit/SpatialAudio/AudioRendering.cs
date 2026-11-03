using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.SpatialAudio;

// WHAT THE USER HEARS (the User Agent's, the author, 2026/10/04: "AOD is UA's
// business"; formerly the AIM CAE-AOD). It renders, and does not play: the
// Loudspeaker Unit plays what it gives.
//
//   - A Basic Audio Object is heard as it is, unless it states where it is heard from
//     (its UserPoV): then it is heard from there, its directional pattern counting.
//   - An Audio Object is heard as a scene of its Basic Audio Objects, from its UserPoV.
//   - A Basic Audio Scene or an Audio Scene is rendered from its UserPoV - each member
//     from its own, if it has one - by the spatial renderer (Steam Audio): distance,
//     air, directional patterns, binaural or onto a speaker layout.
//
// The result is a Basic Audio Object: the rendered audio as a 16-bit PCM WAV at 48 kHz.
// The Header of what is given says which kind it is.
public sealed class AudioRendering(string steamAudioFolder, SpatialRenderer.Layout layout = SpatialRenderer.Layout.Binaural)
{
    public bool Available => SpatialRenderer.Available(steamAudioFolder);

    public BasicAudioObject Render(string json)
    {
        switch (HeaderOf(json))
        {
            case "OSD-BAS-V1.5": return Render(SceneAudio.Members(MpaiJson.FromJson<BasicAudioSceneDescriptors>(json)).ToList(), "scene", RoomOf(json));
            case "OSD-ASD-V1.5": return Render(SceneAudio.Members(MpaiJson.FromJson<AudioSceneDescriptors>(json)).ToList(), "scene", RoomOf(json));
            case "OSD-AUO-V1.5":
                var auo = MpaiJson.FromJson<AudioObject>(json);
                return Render(SceneAudio.Members(auo).ToList(), auo.AudioObjectID);
            case "OSD-BAO-V1.5":
                var bao = MpaiJson.FromJson<BasicAudioObject>(json);
                return bao.UserPoV is null ? bao
                     : Render([new SceneAudio.Member(bao, new double[3], 0, 0, bao.UserPoV)], bao.BasicAudioObjectID);
            default: throw new ArgumentException($"nothing to hear in {HeaderOf(json) ?? "data without a Header"}: an Audio Object or an Audio Scene is heard.");
        }
    }

    // The members as the user hears them: each group of members heard from the same
    // place is rendered, and the groups are mixed.
    private BasicAudioObject Render(IReadOnlyList<SceneAudio.Member> members, string what, RoomAcoustics? room = null)
    {
        if (!Available)
            throw new InvalidOperationException($"no spatial renderer - Steam Audio is not in {steamAudioFolder}.");

        using var renderer = new SpatialRenderer(steamAudioFolder, layout);
        float[] mix = [];
        foreach (var group in members.GroupBy(m => m.User))
        {
            var sources = group.Select(m => new SpatialRenderer.Source(
                SceneAudio.Shape(SceneAudio.WithLevel(m.Object, SceneAudio.Mono(m.Object.Data, SpatialRenderer.Rate), SpatialRenderer.Rate), m.StartSeconds, m.EndSeconds, SpatialRenderer.Rate), m.Position, m.YawDeg, m.PitchDeg, 1,
                DirectionalPattern.FromPlot(m.Object.BasicAudioObjectProperties?.AcousticProfile?.DirectionalPatterns))).ToList();
            var part = renderer.Render(sources, SceneAudio.UserOf(group.Key), room);
            if (part.Length > mix.Length) Array.Resize(ref mix, part.Length);
            for (var i = 0; i < part.Length; i++) mix[i] += part[i];
        }

        if (room is not null && mix.Length > 0 && mix.Max(Math.Abs) is var peak && peak > 0.95f)      // a room adds sound: the whole is brought under the top, not clipped
            for (var i = 0; i < mix.Length; i++) mix[i] *= 0.95f / peak;

        var qualifier = new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(),
            SubTypes = "Mixed",   // a rendering is a mix of its sources
            Formats = new AudioFormats
            {
                ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = SpatialRenderer.Rate, Precision = 16 } } },
                TransportFormat = new AudioTransportFormat { FileFormats = AudioFileFormat.Wav }
            },
            Attributes = new AudioAttributes
            {
                Device = new AudioDevice
                {
                    DeviceRole = "Render",
                    CaptureConfiguration = new CaptureConfiguration { ChannelCount = renderer.Channels, SamplingMode = renderer.Channels switch { 1 => "Mono", 2 => "Stereo", _ => "MultiChannel" } }
                }
            }
        };
        var played = BasicAudioObject.FromData(SceneAudio.Wav(mix, renderer.Channels, SpatialRenderer.Rate), qualifier);
        return new BasicAudioObject
        {
            BasicAudioObjectID = played.BasicAudioObjectID,
            BasicAudioObjectData = played.BasicAudioObjectData,
            AudioQualifier = played.AudioQualifier,
            DescrMetadata = $"{what} rendered for the user ({layout})"
        };
    }

    // THE ROOM OF A SCENE, when the scene has one and an Acoustic Profile of its own: its walls (the Closed Space) and what they do to sound (the Profile). No profile, no room heard: the scene is heard dry.
    // What the profile does not say is the usual of a furnished room: Absorption 0.2 (for a wall that has no Material of its own either), Diffusion 0.5, the first reflection 0.02 s after the
    // direct sound and the late sound 0.04 s after the first reflection (M3282 ACP-1 to ACP-3).
    public static RoomAcoustics? RoomOf(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var scene = node?["SubAudioScenes"] is JsonArray subs
                ? subs.SelectMany(s => s?["SubASceneIDOrSubAScene"] is JsonArray inside ? inside.AsEnumerable() : [s?["BasicScene"]]).FirstOrDefault(b => b is JsonObject o && o["AcousticProfile"] is not null)
                : node;
            if (scene?["AcousticProfile"] is not JsonObject profile) return null;
            JsonObject? box = null;
            void Walk(JsonNode? n)
            {
                if (box is not null) return;
                switch (n)
                {
                    case JsonObject o:
                        if (o["RightParallelepiped"] is JsonObject r && r["CartPositions"] is JsonArray) { box = r; return; }
                        foreach (var kv in o) Walk(kv.Value);
                        break;
                    case JsonArray a:
                        foreach (var e in a) Walk(e);
                        break;
                }
            }
            Walk(scene["ClosedSpace"]);
            if (box?["CartPositions"] is not JsonArray c || c.Count < 4) return null;
            double V(int i, int k) => c[i]![k]!.GetValue<double>();
            double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
            var reflectivity = profile["Reflectivity"] is JsonArray { Count: > 0 } rf ? rf[0] : null;
            // The RT60 is a Plot of pairs (frequency in Hz, time in seconds); the pair nearest to 1000 Hz, and the only one applies at all frequencies.
            double? rt60 = null;
            if (profile["Reverberation"] is JsonArray { Count: > 0 } rv && rv[0]?["RT60"]?["Plot"]?["2D"] is JsonArray pairs)
            {
                var nearest = pairs.OfType<JsonArray>().Where(p => p.Count >= 2 && Number(p[0]) is not null && Number(p[1]) is not null).MinBy(p => Math.Abs(Number(p[0])! .Value - 1000));
                rt60 = nearest is null ? null : Number(nearest[1]);
            }
            // The wall by wall absorption: the Material of its own (the Walls of the Acoustic Profile), else the Absorption of the profile, else its DefaultMaterial, else the usual.
            // A wall is a face of the box (a Region of a face is not a whole face); a flat Polygon that lies on a face of the box is that face; a SurfaceID, or a Polygon that lies on none, has no
            // face here and is left out.
            var byFace = new Dictionary<string, double>();
            if (profile["Walls"] is JsonArray walls)
                foreach (var wall in walls.OfType<JsonObject>())
                {
                    if (wall["Region"] is not null || MaterialAbsorption(wall["MaterialIDOrMaterial"]) is not { } own) continue;
                    var faceName = wall["Face"]?.GetValue<string>() ?? (wall["Polygon"] is JsonArray polygon ? FaceOfPolygon(polygon, V(0, 0), V(1, 0), V(0, 1), V(2, 1), V(0, 2), V(3, 2)) : null);
                    if (faceName is not null) byFace[faceName] = own;
                }
            var fallback = Number(profile["Absorption"]) ?? MaterialAbsorption(profile["DefaultMaterial"]) ?? 0.2;
            double Face(string name) => byFace.TryGetValue(name, out var a) ? a : fallback;
            // The faces in the order of the images: x0 Left, x1 Right, y0 Back, y1 Front, z0 Floor, z1 Ceiling (Right Parallelepiped: Right is the second vertex, Front the third, Up the fourth).
            double[] faces = [Face("Left"), Face("Right"), Face("Back"), Face("Front"), Face("Floor"), Face("Ceiling")];
            return new RoomAcoustics(V(0, 0), V(1, 0), V(0, 1), V(2, 1), V(0, 2), V(3, 2), faces, rt60, Number(profile["Diffusion"]) ?? 0.5,
                Number(reflectivity?["EarlyReflectionTime"]) ?? 0.02, Number(reflectivity?["LateReflectionTime"]) ?? 0.04);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // The face of the box a flat Polygon lies on: all its vertices on one of the six planes of the box (within a centimetre), else null.
    private static string? FaceOfPolygon(JsonArray polygon, double x0, double x1, double y0, double y1, double z0, double z1)
    {
        var points = polygon.OfType<JsonArray>().Where(p => p.Count >= 3).Select(p => new[] { p[0]!.GetValue<double>(), p[1]!.GetValue<double>(), p[2]!.GetValue<double>() }).ToList();
        if (points.Count < 3) return null;
        bool On(int axis, double at) => points.All(p => Math.Abs(p[axis] - at) < 0.01);
        return On(0, x0) ? "Left" : On(0, x1) ? "Right" : On(1, y0) ? "Back" : On(1, y1) ? "Front" : On(2, z0) ? "Floor" : On(2, z1) ? "Ceiling" : null;
    }

    // The absorption of a Material (OSD-MTR, in full: one entry of a MaterialIDOrMaterial): the mean of its fractions over the bands; null for one given by its identifier, which is not known here.
    private static double? MaterialAbsorption(JsonNode? materialOrId)
    {
        var material = materialOrId is JsonArray { Count: > 0 } one ? one[0] : materialOrId;
        if (material is not JsonObject m || (m["Acoustic"]?["Absorption"] ?? m["Absorption"]) is not JsonArray bands) return null;      // OSD-MTR: how the surface acts on sound is in Acoustic
        var fractions = bands.OfType<JsonObject>().Select(b => b["Fraction"] is JsonValue v && v.TryGetValue<double>(out var d) ? (double?)d : null).Where(d => d is not null).Select(d => d!.Value).ToList();
        return fractions.Count > 0 ? fractions.Average() : null;
    }

    private static string? HeaderOf(string json)
    {
        try { return JsonNode.Parse(json)?["Header"]?.GetValue<string>(); }
        catch { return null; }
    }
}
