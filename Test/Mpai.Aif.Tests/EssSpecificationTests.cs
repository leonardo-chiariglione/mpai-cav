using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Metadata;

using Json.Schema;

namespace Mpai.Aif.Tests;

// THE SPECIFICATION OF THE ENVIRONMENT SENSING SUBSYSTEM, CAV V2.0 (M3229): its L2s,
// as a Standard states them, before any of it is implemented. Every L2 validates
// against the AIM Metadata schema; the composites are wired as their Sub-AIMs
// declare - every label declared, every Sub-AIM an L2 of the repository, every end
// a Port of that Direction whose Data Types include the label's, every required
// input of every Sub-AIM fed; and the Basic Environment Descriptors V2.0 describe
// an environment.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class EssSpecificationTests
{
    private static readonly string[] L2s =
    [
        "CAV2/V2.0/AIMs/EnvironmentSensingSubsystem.json",
        "CAV2/V2.0/AIMs/AudioSensors.json", "CAV2/V2.0/AIMs/VisualSensors.json", "CAV2/V2.0/AIMs/RADARSensors.json",
        "CAV2/V2.0/AIMs/LiDARSensors.json", "CAV2/V2.0/AIMs/UltrasoundSensors.json",
        "CAV2/V2.0/AIMs/SpatialAttitudeGeneration.json", "CAV2/V2.0/AIMs/BasicEnvironmentDescription.json",
        "CAE3/V1.0/AIMs/AudioObjectAcquisition.json", "CVE/V1.0/AIMs/VisualObjectAcquisition.json",
        "OSD/V1.5/AIMs/RADARObjectAcquisition.json", "OSD/V1.5/AIMs/LiDARObjectAcquisition.json",
        "OSD/V1.5/AIMs/UltrasoundObjectAcquisition.json",
        "OSD/V1.5/AIMs/BasicAudioSceneDescription.json", "OSD/V1.5/AIMs/BasicVisualSceneDescription.json",
        "OSD/V1.5/AIMs/BasicRADARSceneDescription.json", "OSD/V1.5/AIMs/BasicLiDARSceneDescription.json",
        "OSD/V1.5/AIMs/BasicUltrasoundSceneDescription.json", "OSD/V1.5/AIMs/BasicOfflineMapSceneDescription.json"
    ];

    private static JsonObject Load(string relative) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, relative)))!.AsObject();

    // The L2s of the repository by Header.
    private static Dictionary<string, JsonObject> AllL2s()
    {
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Repository.Schemas, "*.json", SearchOption.AllDirectories)
                                      .Where(f => f.Replace('\\', '/').Contains("/AIMs/")))
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                var header = (string?)o?["Header"];
                if (header is not null) found.TryAdd(header, o!);
            }
            catch (JsonException) { }
        }
        return found;
    }

    private static IEnumerable<string> Types(JsonNode? dataType) =>
        dataType is JsonArray a ? a.Select(t => (string)t!) : [(string)dataType!];

    [Fact]
    public void L2sValidate()
    {
        var schema = AimMetadataSchema.Load(Repository.Schemas);
        var result = new Dictionary<string, string>();
        foreach (var l2 in L2s)
        {
            var violations = schema.Violations(File.ReadAllText(Path.Combine(Repository.Schemas, l2)));
            result[l2] = violations.Count == 0 ? "valid" : string.Join("; ", violations);
        }
        Expected.Match("ess-v2-schema.json", result);
    }

    [Fact]
    public void CompositesAreWired()
    {
        var l2s = AllL2s();
        var result = new Dictionary<string, string>();
        foreach (var file in L2s.Where(f => Load(f)["SubAIMs"] is JsonArray { Count: > 0 }))
        {
            var composite = Load(file);
            var problems = new List<string>();

            // The composite's labels: its ExternalPorts and InternalTypes.
            var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var p in composite["ExternalPorts"]!.AsArray().Concat(composite["InternalTypes"]?.AsArray() ?? []))
                labels[(string)p!["Name"]!] = Types(p["DataType"]).ToList();

            var subs = composite["SubAIMs"]!.AsArray().Select(s => (string)s!["Identifier"]!["AIMName"]!).ToList();
            foreach (var s in subs.Where(s => !l2s.ContainsKey(s))) problems.Add($"{s} has no L2");

            var fed = new HashSet<(string Aim, string Type)>();
            foreach (var line in composite["Topology"]!.AsArray())
            {
                foreach (var (end, direction) in new[] { (line!["Output"]!, "Output"), (line["Input"]!, "Input") })
                {
                    var aim = (string)end["AIMName"]!;
                    var label = (string)end["PortName"]!;
                    if (!labels.TryGetValue(label, out var types)) { problems.Add($"label {label} not declared"); continue; }
                    if (aim == "")
                    {
                        // The boundary: an Output end is one of the composite's Inputs, and the reverse.
                        var boundary = composite["ExternalPorts"]!.AsArray().FirstOrDefault(p => (string)p!["Name"]! == label);
                        var want = direction == "Output" ? "Input" : "Output";
                        if (boundary is null || (string)boundary["Direction"]! != want) problems.Add($"{label} is not a boundary {want}");
                        continue;
                    }
                    if (!l2s.TryGetValue(aim, out var subL2)) continue;
                    var ports = subL2["ExternalPorts"]!.AsArray().Where(p => (string)p!["Direction"]! == direction
                                   && types.All(t => Types(p["DataType"]).Contains(t))).ToList();
                    if (ports.Count == 0) problems.Add($"{aim} has no {direction} of {string.Join("|", types)} ({label})");
                    if (direction == "Input") foreach (var t in types) fed.Add((aim, t));
                }
            }

            // Every required input of every Sub-AIM is fed: in continuous execution an
            // AIM waits for its required inputs (M3230 11.3).
            foreach (var s in subs.Where(l2s.ContainsKey))
                foreach (var p in l2s[s]["ExternalPorts"]!.AsArray().Where(p => (string)p!["Direction"]! == "Input" && p["IsOptional"]?.GetValue<bool>() != true))
                    if (!Types(p!["DataType"]).Any(t => fed.Contains((s, t)))) problems.Add($"{s}'s required {(string)p["Name"]!} is not fed");

            result[(string)composite["Header"]!] = problems.Count == 0 ? "wired" : string.Join("; ", problems.Distinct());
        }
        Expected.Match("ess-v2-wiring.json", result);
    }

    // An environment of two objects - a vehicle ahead seen by camera and RADAR, and the
    // lane it is in from the Offline Map - validates; without the ego Spatial Attitude
    // it does not.
    [Fact]
    public void BasicEnvironmentDescriptorsDescribe()
    {
        var schemas = PublishedSchemas.At(Repository.Schemas);
        var schema = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "BasicEnvironmentDescriptors.json"))];
        const string time = """{ "Header": "OSD-STM-V1.5", "SimpleTimeID": "t", "SimpleTimeData": [ { "FlagsByte": 0, "StartTime": 1000, "EndTime": 1000, "AccuracyPlusMinus": 0, "AccuracyStartPlusMinus": 0, "AccuracyEndPlusMinus": 0 } ] }""";
        var sa = """{ "Header": "OSD-OSA-V1.5", "ObjectSpatialAttitudeID": "ego", "Position": { "Header": "OSD-OPS-V1.5", "PositionID": "p", "CartPosition": [0, 0, 0] }, "Orientation": { "Header": "OSD-OOR-V1.5", "OrientationID": "o", "Orientation": [0, 0, 0] } }""";
        var instance = $$"""
            { "Header": "CAV-BED-V2.0", "BasicEnvironmentDescriptorsID": "bed-1", "BasicEnvironmentDescriptorsTime": {{time}},
              "EgoSpatialAttitude": {{sa}}, "BasicEnvironmentObjectCount": 2,
              "BasicEnvironmentObjects": [
                { "BasicEnvironmentObjectID": "track-7", "SpatialAttitude": {{sa}}, "ExistenceConfidence": 0.96, "Motion": "Dynamic",
                  "Contributions": [ { "Technology": "Visual", "SceneDescriptorsID": "bvs-41", "ObjectID": "v3" },
                                     { "Technology": "RADAR",  "SceneDescriptorsID": "brs-40", "ObjectID": "r1" } ] },
                { "BasicEnvironmentObjectID": "lane-2", "SpatialAttitude": {{sa}}, "ExistenceConfidence": 1, "Motion": "Static",
                  "Contributions": [ { "Technology": "OfflineMap" } ] } ] }
            """;
        var result = new Dictionary<string, string>();
        foreach (var (name, json) in new[] { ("two objects", instance), ("no ego Spatial Attitude", instance.Replace("\"EgoSpatialAttitude\"", "\"Unknown\"")) })
        {
            EvaluationResults evaluation;
            using var doc = JsonDocument.Parse(json);
            lock (PublishedSchemas.Lock) evaluation = schema.Evaluate(doc.RootElement);
            result[name] = evaluation.IsValid ? "valid" : "not valid";
        }
        Expected.Match("ess-v2-bed.json", result);
    }
}
