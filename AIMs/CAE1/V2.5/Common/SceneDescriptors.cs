using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// A sound source of an Audio Scene: where it is heard from (azimuth anticlockwise from the x axis and elevation above the xy plane, degrees, from the centre of the array) and when it is active.
public sealed record SceneSource(string Id, double Azimuth, double Elevation, (double Start, double End)[] Active);

// Basic Audio Scene Descriptors (OSD-BAS-V1.5) as the AIMs of Enhanced Audioconference Experience give them - the Audio Scene Geometry of Speech Detection and Separation and the Audio Scene
// Descriptors of Audio Mixing. Each source is a member of the scene: its ID (the Audio Object, which is elsewhere), its Space-Time (the Spatial Attitude with the Position as a spherical
// position: the distance is 1 as nothing in the sound field says how far a source is, then azimuth, then elevation, in degrees) and the Time of its Simple Time as the segments, in seconds, it
// is active in.
public static class SceneDescriptors
{
    public const string Header = "OSD-BAS-V1.5";

    public static string Make(string id, IReadOnlyList<SceneSource> sources)
    {
        var members = new JsonArray();
        foreach (var s in sources)
        {
            var segments = new JsonArray();
            foreach (var (start, end) in s.Active)
                segments.Add(new JsonObject { ["FlagsByte"] = 0, ["StartTime"] = Math.Round(start, 4), ["EndTime"] = Math.Round(end, 4), ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0.0, ["TimeType"] = false, ["TimeUnit"] = "00", ["Reserved"] = 0 });
            var spaceTime = new JsonObject
            {
                ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = "spt-" + s.Id,
                ["SpatialAttitude1"] = new JsonObject
                {
                    ["Header"] = "OSD-OSA-V1.5", ["ObjectSpatialAttitudeID"] = "osa-" + s.Id,
                    ["General"] = new JsonObject { ["CoordType"] = "Spherical", ["MediaType"] = "Audio" },
                    ["Position"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = "pos-" + s.Id, ["SpherPosition"] = new JsonArray(1.0, Math.Round(s.Azimuth, 2), Math.Round(s.Elevation, 2)) },
                    ["Orientation"] = new JsonObject { ["Header"] = "OSD-OOR-V1.5", ["OrientationID"] = "or-" + s.Id, ["Orientation"] = new JsonArray(0, 0, 0) }
                }
            };
            if (segments.Count > 0) spaceTime["Time"] = new JsonObject { ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = "stm-" + s.Id, ["SimpleTimeData"] = segments };
            members.Add(new JsonObject { ["AudioObjectSpaceTime"] = spaceTime, ["AObjectIDOrAObject"] = new JsonArray(s.Id) });
        }
        return new JsonObject
        {
            ["Header"] = Header, ["MInstanceID"] = "m-1", ["BasicAudioSceneDescriptorsID"] = id,
            ["BASSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = "spt-scene" },
            ["AudioObjectCount"] = sources.Count, ["BasicAudioSceneDescriptorsData"] = members,
            ["UserPoV"] = new JsonObject { ["Header"] = "OSD-OPV-V1.5", ["PointOfViewID"] = "pov-array", ["CartPosition"] = new JsonArray(0, 0, 0), ["Orientation"] = new JsonArray(0, 0, 0) }
        }.ToJsonString();
    }

    public static IReadOnlyList<SceneSource> Read(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Audio Scene Descriptors are a JSON object.");
        if (root["Header"]?.GetValue<string>() != Header) throw new FormatException($"Not Basic Audio Scene Descriptors: Header {root["Header"]}.");
        var result = new List<SceneSource>();
        foreach (var m in (root["BasicAudioSceneDescriptorsData"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = (m["AObjectIDOrAObject"] as JsonArray)?.FirstOrDefault() is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var attitude = m["AudioObjectSpaceTime"]?["SpatialAttitude1"];
            var polar = attitude?["Position"]?["SpherPosition"] as JsonArray ?? throw new FormatException($"The member '{id}' has no spherical position.");
            var active = (m["AudioObjectSpaceTime"]?["Time"]?["SimpleTimeData"] as JsonArray ?? []).OfType<JsonObject>().Select(t => (Number(t["StartTime"]), Number(t["EndTime"]))).ToArray();
            result.Add(new SceneSource(id, Number(polar[1]), Number(polar[2]), active));
        }
        return result;
    }

    private static double Number(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);
}
