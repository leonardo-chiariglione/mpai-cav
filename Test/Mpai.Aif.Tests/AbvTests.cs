using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;
using Mpai.Paf.Pdx;
using Mpai.Paf.Pmx;
using Mpai.Paf.Vcr;

namespace Mpai.Aif.Tests;

// THE AVATAR-BASED VIDEOCONFERENCE FROM END TO END, on perfect inputs - no camera, no microphone, no models, no network - so that it runs in a moment: four participants and
// the Virtual Meeting Secretary, each on its own PC with a Videoconference Client Transmitter (the PMX of it) and a Videoconference Client Receiver, and one Avatar Videoconference
// Server between them. Where the author decided that it is not run for the time being, it is left out: no authentication (the Avatar ID is the participant's own, that
// registration gave), no translation. The AIMs are the repository's (PMX, PDX, VCR); the Controller's firing is not in this test (the AIMs are called as the Controller calls them).
//
//   VCT: the participant's Portable Avatar - ID, and at the introduction the Avatar Model and the Language Selector; later what changed (what is said, the Personal Status);
//   AVS: PDX gives the ID of the Portable Avatar (as Service Participant Authentication would), PMX puts into the Portable Avatar - which it starts from - the Audio-Visual Scene
//        Descriptors of the room and the Spatial Attitude of the seat of the participant, and the ID;
//   VCR: each PC holds the avatars by ID, seats them in the Scene and adds their speech at the mouth.
public class AbvTests
{
    private static readonly string[] Participants = ["anna", "bruno", "carla", "dario"];
    private const string Secretary = "Secretary";

    // The seats of the table: five places on a circle of 1.5 m, the secretary the fifth.
    private static (double X, double Y) Seat(string who)
    {
        var i = Array.IndexOf(Participants, who); if (i < 0) i = Participants.Length;
        return (Math.Round(1.5 * Math.Cos(i * 2 * Math.PI / 5), 3), Math.Round(1.5 * Math.Sin(i * 2 * Math.PI / 5), 3));
    }

    private sealed class Aims
    {
        public readonly AimPortReader PmxPorts, PdxPorts;
        public readonly PmxAimProcessor Pmx;
        public readonly PdxAimProcessor Pdx;

        public Aims()
        {
            var store = new AIF.Store.AmdStore(Repository.Amds); store.Scan();
            PmxPorts = AimPortReader.Load(store, "1PAF-PMX-V1.6-I01");
            PdxPorts = AimPortReader.Load(store, "1PAF-PDX-V1.6-I01");
            Pmx = new PmxAimProcessor("1PAF-PMX-V1.6-I01", PmxPorts);
            Pdx = new PdxAimProcessor("1PAF-PDX-V1.6-I01", PdxPorts);
        }

        public async Task<JsonObject> Mux(Dictionary<string, JsonNode> inputs)
        {
            var made = await Pmx.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = inputs.ToDictionary(p => p.Key, p => p.Value.ToJsonString()) });
            return JsonNode.Parse(made.Ports[PmxPorts.Output("PAF-PAV-V1.6")])!.AsObject();
        }

        public async Task<Dictionary<string, JsonNode?>> Demux(JsonObject pav)
        {
            var made = await Pdx.ProcessAsync(new Message { MessageId = Guid.NewGuid().ToString(), Ports = new() { [PdxPorts.Input("PAF-PAV-V1.6")] = pav.ToJsonString() } });
            return made.Ports.ToDictionary(p => p.Key, p => JsonNode.Parse(p.Value));
        }
    }

    private static JsonObject Model(string who) => new() { ["Header"] = "OSD-B3O-V1.5", ["Basic3DModelObjectID"] = "model-of-" + who, ["Basic3DModelObjectData"] = new JsonArray(new JsonObject { ["Data"] = "AAEC" }) };
    private static JsonObject Speech(string who, int n) => new() { ["Header"] = "OSD-BSO-V1.5", ["BasicSpeechObjectID"] = $"speech-{who}-{n}" };

    // The room: the Audio-Visual Scene Descriptors of the meeting, which the participants' Portable Avatars do not have and the server adds.
    private static JsonObject Room() => new()
    {
        ["Header"] = "OSD-BMS-V1.5", ["MInstanceID"] = "", ["BasicAVSceneDescriptorsID"] = "meeting-room",
        ["BAVSDescriptorsSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["MInstanceID"] = "", ["SpaceTimeID"] = "room-st" }
    };

    // THE VCT of a participant: the Portable Avatar. The introduction carries the ID, the model and the language; an update, the ID and what it brings.
    private static Task<JsonObject> Vct(Aims a, string who, bool introduction, string? says, int n) => a.Mux(new Dictionary<string, JsonNode>
    {
        [a.PmxPorts.InputOrDefault("string", "")] = JsonValue.Create(who)!,
        [a.PmxPorts.InputOrDefault("OSD-B3O-V1.5", "")] = introduction ? Model(who) : null!,
        [a.PmxPorts.InputOrDefault("OSD-SEL-V1.5", "")] = introduction ? new JsonObject { ["Header"] = "OSD-SEL-V1.5", ["SelectorID"] = "english" } : null!,
        [a.PmxPorts.InputOrDefault("OSD-BSO-V1.5", "")] = says is null ? null! : Speech(who, n)
    }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

    // THE AVS: what the Portable Avatar has comes through; the server adds the room and the seat, and the ID Service Participant Authentication gave.
    private static async Task<JsonObject> Avs(Aims a, JsonObject fromParticipant)
    {
        var parts = await a.Demux(fromParticipant);
        var id = (string)parts[a.PdxPorts.OutputOrDefault("string", "")]!;                   // what PDX gives; authentication is not run, so it is the ID the Portable Avatar carries
        var (x, y) = Seat(id);
        return await a.Mux(new Dictionary<string, JsonNode>
        {
            [a.PmxPorts.InputOrDefault("PAF-PAV-V1.6", "")] = fromParticipant,
            [a.PmxPorts.InputOrDefault("string", "")] = JsonValue.Create(id)!,
            [a.PmxPorts.InputOrDefault("OSD-BMS-V1.5", "")] = Room(),
            [a.PmxPorts.InputOrDefault("OSD-OSA-V1.5", "")] = EssJson.Attitude($"osa-{id}", 100, (x, y, 0), (0.1, 0.1, 0.1), (0, 0, 0))
        });
    }

    // The VCR of one PC: what it gives out, in the order it was given out.
    private static async Task<List<(string DataType, JsonNode Json)>> Vcr(IEnumerable<JsonObject> arrivals)
    {
        var drive = new DrivePorts(arrivals.Select((p, i) => (TimeSpan.FromMilliseconds(100 * (i + 1)), "PAF-PAV-V1.6", 1, p.ToJsonString())));
        await new VideoconferenceClientReceiver("1PAF-VCR-V1.6-I01").RunAsync(drive, AimContext.None);
        return drive.Written.Select(w => (w.DataType, JsonNode.Parse(w.Json)!)).ToList();
    }

    private static List<JsonNode> Members(JsonNode scene) => scene["BasicObjectsData"]!.AsArray().Select(m => m!).ToList();
    private static string Id(JsonNode member) => (string)member["BXOOrBXOID"]!;
    private static JsonArray At(JsonNode member) => member["BXOSpaceTime"]!["SpatialAttitude1"]!["Position"]!["CartPosition"]!.AsArray();

    [Fact]
    public async Task TheMeetingFromEndToEnd()
    {
        var a = new Aims();

        // THE INTRODUCTIONS: each participant sends its Portable Avatar to the server (the VCT), which seats it and gives the room (the AVS). The secretary is a participant too:
        // a pure avatar that the server seats from the start.
        var introduced = new List<JsonObject>();
        foreach (var who in Participants.Append(Secretary))
        {
            var pav = await Vct(a, who, introduction: true, says: null, n: 0);
            Assert.Equal(who, (string)pav["AvatarOrAvatarID"]!.AsArray().OfType<JsonValue>().Single());                  // the ID, and the model beside it
            Assert.NotNull(pav["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().SingleOrDefault());
            Assert.Null(pav["AVSceneDescriptors"]);                                                                     // the participant's avatar has no room
            Assert.Null(pav["AvatarSpatialAttitude"]);                                                                  // and no seat
            var served = await Avs(a, pav);
            Assert.Equal("meeting-room", (string)served["AVSceneDescriptors"]!["BasicAVSceneDescriptorsID"]!);          // the server added the room
            Assert.Equal($"osa-{who}", (string)served["AvatarSpatialAttitude"]!["ObjectSpatialAttitudeID"]!);           // and the seat
            Assert.NotNull(served["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().SingleOrDefault());               // the model the participant sent was not lost
            introduced.Add(served);
        }

        // THE MEETING GOES ON: later Portable Avatars are updates - the ID and what changed. Anna and the secretary speak.
        var updates = new List<JsonObject>();
        foreach (var (who, n) in new[] { ("anna", 1), (Secretary, 1), ("anna", 2) })
        {
            var pav = await Vct(a, who, introduction: false, says: "hello", n: n);
            Assert.Equal([who], pav["AvatarOrAvatarID"]!.AsArray().Select(x => (string)x!));                            // no model again: it comes once
            updates.Add(await Avs(a, pav));
        }

        // EACH PC'S VCR gets the same stream and ends with the same Scene: five avatars seated where the server said, the speech at the mouths of those who spoke.
        var given = await Vcr(introduced.Concat(updates));
        var scene = given.Last(g => g.DataType == "OSD-BMS-V1.5").Json;
        var members = Members(scene);
        Assert.Equal(["anna", "bruno", "carla", "dario", Secretary, "speech-anna-2", "speech-Secretary-1"], members.Select(Id));
        foreach (var who in Participants.Append(Secretary))
        {
            var at = At(members.Single(m => Id(m) == who));
            Assert.Equal(Seat(who).X, (double)at[0]!, 3);
            Assert.Equal(Seat(who).Y, (double)at[1]!, 3);
        }
        var mouth = At(members.Single(m => Id(m) == "speech-anna-2"));
        Assert.Equal((double)At(members.Single(m => Id(m) == "anna"))[2]! + 1.6, (double)mouth[2]!, 3);                // the last thing Anna said, at her mouth

        // The Portable Avatar the VCR gives out for an update has the model and the seat of the introduction, and what the update brought.
        var last = given.Last(g => g.DataType == "PAF-PAV-V1.6").Json;
        Assert.Equal("anna", (string)last["AvatarOrAvatarID"]!.AsArray().OfType<JsonValue>().Single());
        Assert.Equal("model-of-anna", (string)last["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().Single()["Basic3DModelObjectID"]!);
        Assert.Equal("speech-anna-2", (string)last["SPOOrSPOID"]![0]!["BasicSpeechObjectID"]!);
        Assert.Equal(1, scene["BasicObjectsData"]!.AsArray().Count(m => Id(m!) == "anna"));                              // one Anna in the Scene

        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "OSD", "V1.5", "data", "BasicAudioVisualSceneDescriptors.json"))];
        foreach (var g in given.Where(g => g.DataType == "OSD-BMS-V1.5"))
        {
            using var doc = JsonDocument.Parse(g.Json.ToJsonString());
            lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(schema.Evaluate(doc.RootElement).IsValid, g.Json.ToJsonString());
        }
    }

    // A participant that has not sent a model is shown as the default avatar - the worst case is a static avatar.
    [Fact]
    public async Task AParticipantWithoutAModelIsTheDefaultAvatar()
    {
        var a = new Aims();
        var pav = await Vct(a, "anna", introduction: false, says: null, n: 0);
        var given = await Vcr([await Avs(a, pav)]);
        var avatar = given.Single(g => g.DataType == "PAF-PAV-V1.6").Json;
        Assert.Equal("default-avatar", (string)avatar["AvatarOrAvatarID"]!.AsArray().OfType<JsonObject>().Single()["Basic3DModelObjectID"]!);
        Assert.Equal(["anna"], Members(given.Single(g => g.DataType == "OSD-BMS-V1.5").Json).Select(Id));
    }
}
