using System.Text.Json.Nodes;
using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// WHAT A REMOTE CLIENT CAN GIVE AND RECEIVE. Over MPAI-MAS a Datum crosses only if a
// codec gives it a wire form, and the Remote Client Application refuses any other
// Data Type. When RSR took an Avatar and gave a Speaking Avatar (2026/10/03), neither
// had a codec: every App was out of reach over MPAI-MAS, and only the Service tests -
// not in the ordinary matrix - would have said so (found testing the Linux update,
// 2026/10/04). Judged here, fast: every boundary Port of every Module of MAS-App has a
// Data Type the codecs know, and the Avatar and the Speaking Avatar go to the wire and
// back unchanged.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class MasCodecsTests
{
    private static readonly string[] Modules =
        ["1MMC-MAD-V2.5-I01", "1MMC-AMQ-V2.5-I01", "1MMC-MAT-V2.5-I01", "1MMC-MPD-V2.5-I01", "1MMC-MAC-V2.5-I01", "1MMC-ACR-V2.5-I01"];

    [Fact]
    public void EveryBoundaryPortCrosses()
    {
        var codecs = PortDataCodecs.Default();
        var result = new Dictionary<string, string>();
        foreach (var module in Modules)
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, module + ".json")))!.AsObject();
            var missing = new List<string>();
            foreach (var port in l3["ExternalPorts"]!.AsArray().Select(p => p!.AsObject()))
            {
                var types = port["DataType"] is JsonArray many ? many.Select(t => (string)t!).ToList() : [(string)port["DataType"]!];
                if (!types.Any(codecs.Knows)) missing.Add($"{port["Direction"]} {string.Join("|", types)}");
            }
            result[module] = missing.Count == 0 ? "every boundary Port crosses" : "NO CODEC: " + string.Join(", ", missing);
        }

        var avatar = AvatarUtterance.AvatarDatum("cav-avatar.glb");
        result["the Avatar to the wire and back"] = Same(codecs, "PAF-AVT-V1.6", avatar);
        var speaking = MpaiJson.ToJson(new SpeakingAvatar
        {
            SpeakingAvatarID = "s",
            SpeakingAvatarData = new SpeakingAvatarData
            {
                Avatar = MpaiJson.FromJson<Avatar>(avatar).Speaking(null, new FaceDescriptorsObject { FaceDescriptorsObjectID = "f" }, null),
                SpeechObject = new BasicSpeechObject { BasicSpeechObjectID = "hello", Data = new byte[64] }
            }
        });
        result["the Speaking Avatar to the wire and back"] = Same(codecs, "XRV-SAV-V1.0", speaking);
        Expected.Match("mas-codecs.json", result);
    }

    private static string Same(PortDataCodecs codecs, string dataType, string json)
    {
        var back = codecs.ToInternal(dataType, codecs.ToWire(dataType, json));
        return JsonNode.DeepEquals(JsonNode.Parse(back), JsonNode.Parse(json)) ? "unchanged" : "CHANGED";
    }
}
