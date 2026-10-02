using System.Text.Json;
using System.Text.Json.Nodes;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Mmc.Edp;

namespace Mpai.Aif.Tests;

// EDP EXTENDED FOR AUDIO SCENE MANAGEMENT (the author, 2026/10/03: an AIM that does
// the job, or almost does it, is used or extended). Given the Scene the User edits,
// what the User says becomes User Commands for CAE-ASM. Judged on the 59 requests of
// Test/AsmVoice - plain, paraphrased, relative, two at once, misrecognised, and
// ambiguous ones whose right answer is a question - with the local model EDP uses:
// at least 75% understood right (2026/10/03: 47 of 59, 79.7%, the share in
// Test/Reports/edp-asm-voice.json); every User Command valid against its schema; and
// through EDP's L3, a move, a play, a question, and an undo, which only the User
// Agent does and goes to it as Text For UA (MMC-TFU-V1.5).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class EdpAsmTests
{
    private const string Model = "llama3.2:3b";

    private static bool Ollama()
    {
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; return h.GetAsync("http://127.0.0.1:11434/api/tags").Result.IsSuccessStatusCode; }
        catch { return false; }
    }

    private static SpatialAttitude At(double x, double y, double yaw) => new()
    {
        ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
        Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [x, y, 0] },
        Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, yaw] }
    };

    // The scene of the test set: its members named by their descriptions.
    private static BasicAudioSceneDescriptors Scene(JsonObject members) => new()
    {
        MInstanceID = "ASM", BasicAudioSceneDescriptorsID = "BAS000001", AudioObjectCount = members.Count,
        UserPoV = new PointOfView { PointOfViewID = "user", CartPosition = [0, 0, 0], Orientation = [0, 0, 0] },
        BasicAudioSceneDescriptorsEntries = members.Select((m, i) => new BasicAudioSceneEntry
        {
            AudioObjectSpaceTime = new SpaceTime { SpaceTimeID = $"st{i}", SpatialAttitude1 = At(m.Value![0]!.GetValue<double>(), m.Value![1]!.GetValue<double>(), 180) },
            AudioObjectIDOrAudioObject = new BasicAudioObject { BasicAudioObjectID = $"BAO00000{i + 1}", DescrMetadata = m.Key }
        }).ToList()
    };

    private static bool Matches(JsonObject e, JsonObject g)
    {
        if ((string?)e["act"] != (string?)g["act"]) return false;
        foreach (var k in new[] { "target", "direction", "towards" })
            if (e[k] is not null && (string?)e[k] != (string?)g[k]) return false;
        if (e["amount"] is JsonArray a)
        {
            var v = g["amount_m"] is JsonValue x ? x.GetValue<double>() : 1.0;
            if (v < a[0]!.GetValue<double>() || v > a[1]!.GetValue<double>()) return false;
        }
        if (e["change"] is JsonArray c)
        {
            if (g["change_db"] is not JsonValue x) return false;
            var v = x.GetValue<double>();
            if (v < c[0]!.GetValue<double>() || v > c[1]!.GetValue<double>()) return false;
        }
        return true;
    }

    [SkippableFact]
    public async Task VoiceCommandsForTheScene()
    {
        Skip.IfNot(Ollama(), "Ollama is not running.");
        var set = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Root, "Test", "AsmVoice", "cases.json")))!;
        var members = set["scene"]!["members"]!.AsObject();
        var library = set["scene"]!["library"]!.AsArray().Select(x => (string)x!).ToList();
        var scene = Scene(members);
        var asmMembers = AsmDialogue.MembersOf(scene);
        using var llm = new OllamaClient(Model, context: 4096);
        var dialogue = new AsmDialogue((s, t, schema) => llm.ChatSchemaAsync(s, t, schema));

        var schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
        var ucm = schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAE3/V1.0/data/UserCommand.json"))];
        int right = 0, commands = 0, valid = 0; var wrong = new List<string>();
        foreach (var c in set["cases"]!.AsArray())
        {
            var text = (string)c!["request"]!;
            var expected = c["expected"]!.AsArray().Select(x => x!.AsObject()).ToList();
            var got = await dialogue.UnderstandAsync(text, asmMembers, library, [0, 0, 0], 0);
            var ok = (string?)expected[0]["act"] == "ask"
                ? (string?)got[0]["act"] == "ask"
                : got.Count == expected.Count && expected.Zip(got).All(p => Matches(p.First, p.Second));
            if (ok) right++; else wrong.Add(text);

            var turn = AsmDialogue.Compose(got, scene, asmMembers, [0, 0, 0], 0);
            if (turn.Command is not null)
            {
                commands++;
                using var doc = JsonDocument.Parse(MpaiJson.ToJson(turn.Command));
                lock (AIF.Metadata.PublishedSchemas.Lock) if (ucm.Evaluate(doc.RootElement).IsValid) valid++;
            }
        }
        var share = (double)right / set["cases"]!.AsArray().Count;
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "edp-asm-voice.json"),
            JsonSerializer.Serialize(new { Model, Right = right, Of = set["cases"]!.AsArray().Count, Share = Math.Round(share, 3), Wrong = wrong }, new JsonSerializerOptions { WriteIndented = true }));

        // Through EDP's L3: the Scene and the words in, the User Command and the reply out.
        var store = new AIF.Store.AmdStore(Repository.Amds);
        store.Scan();
        var ports = AIF.Controller.AimPortReader.Load(store, "1MMC-EDP-V2.5-I01");
        var edp = new EdpAimProcessor("1MMC-EDP-V2.5-I01", llm, ports);
        async Task<(string? Reply, JsonNode? Command, JsonNode? ForUA)> Say(string words)
        {
            var o = await edp.ProcessAsync(new AIF.Controller.Message
            {
                MessageId = Guid.NewGuid().ToString(),
                Ports = new() { [ports.Input("OSD-BTO-V1.5")] = MpaiJson.ToJson(BasicTextObject.FromText(words)), [ports.Input("OSD-BAS-V1.5")] = MpaiJson.ToJson(scene) }
            });
            var reply = o.Ports.TryGetValue(ports.Output("OSD-BTO-V1.5"), out var t) ? MpaiJson.FromJson<BasicTextObject>(t)?.GetText() : null;
            var command = o.Ports.TryGetValue(ports.Output("CAE-UCM-V1.0"), out var u) ? JsonNode.Parse(u) : null;
            var forUA = o.Ports.TryGetValue(ports.Output("MMC-TFU-V1.5"), out var f) ? JsonNode.Parse(f) : null;
            return (reply, command, forUA);
        }
        var move = await Say("move the violin to the left");
        var play = await Say("play the scene");
        var ask = await Say("move it to the left");
        var undo = await Say("undo");
        var moved = move.Command?["UserCommandData"]?["MovedObjects"]?["Objects"]?[0]?["NewSpatialAttitude"]?["Position"]?["CartPosition"]?[0];

        Expected.Match("edp-asm-voice.json", new Dictionary<string, string>
        {
            ["the requests understood right, at least 75%"] = share >= 0.75 ? "yes" : $"no ({right} of {set["cases"]!.AsArray().Count})",
            ["the User Commands valid against their schema"] = valid == commands ? "all" : $"{valid} of {commands}",
            ["through EDP: move the violin to the left"] = $"{(moved is not null ? $"the violin to x = {moved.GetValue<double>():0.#}" : "no Moved Object")}; said \"{move.Reply}\"",
            ["through EDP: play the scene"] = play.Command?["UserCommandData"]?["DeliveredObject"] is not null ? "a Delivered Object" : "no command",
            ["through EDP: undo"] = undo.ForUA is not null ? $"Text For UA \"{undo.ForUA["Text"]}\", {(undo.Command is null ? "no User Command" : "a User Command")}" : "no Text For UA",
            ["through EDP: move it to the left"] = ask.Command is null && ask.Reply?.EndsWith('?') == true ? "a question, no command" : $"command {(ask.Command is null ? "none" : "sent")}, said \"{ask.Reply}\""
        });
    }
}
