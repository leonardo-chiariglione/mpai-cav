using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Cae.Usc;

// The Basic Visual Object (OSD-BVO-V1.5) as it carries the Irregularity Images: each data item a still image (PNG), with the ID of the irregularity it shows. The Visual Qualifier
// (TFA-VIQ-V1.5) says what the images are: Formats.Content.2D.Static "PNG".
public static class VisualObject
{
    public const string Header = "OSD-BVO-V1.5";

    public static string Make(string id, IEnumerable<(string Id, byte[] Png)> images) =>
        new JsonObject
        {
            ["Header"] = Header, ["BasicVisualObjectID"] = id,
            ["BasicVisualObjectData"] = new JsonArray(images.Select(i => (JsonNode)new JsonObject { ["ID"] = i.Id, ["Data"] = Convert.ToBase64String(i.Png) }).ToArray()),
            ["VisualQualifier"] = new JsonObject
            {
                ["Header"] = "TFA-VIQ-V1.5", ["VisualQualifierID"] = "q-" + id, ["SubTypes"] = new JsonObject(),
                ["Formats"] = new JsonObject { ["Content"] = new JsonObject { ["2D"] = new JsonObject { ["Static"] = "PNG" } } }
            }
        }.ToJsonString();

    public static IReadOnlyList<(string? Id, byte[] Png)> Images(string json) =>
        AudioObject.Files(json, "BasicVisualObjectData").Select(f => (f.Id, f.Bytes)).ToList();
}
