using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Xrv;

public enum CueKind { Real, Virtual, Mixed }

// What a Cue Condition names: the Status it is to be met by (a Status of one of the six kinds, whose data are compared as JsonMatch says).
public sealed record Target(StatusKind Kind, JsonObject Status)
{
    public JsonNode? Data => Status[KindInfo.Of(Kind).StatusDataKey];
}

// One group of a Cue: the Conditions, the Actions to execute when they are met, and the Cue to go to.
public sealed record CueGroup(CueKind Kind, IReadOnlyList<Target> Targets, bool HasDescriptorConditions,
                              IReadOnlyList<string> REActions, IReadOnlyList<string> VEActions, string? NextCue);

public sealed record Cue(string Id, CueKind Kind, IReadOnlyList<CueGroup> Groups)
{
    // The targets of one kind of Status, in all the groups of the Cue.
    public IEnumerable<Target> TargetsOf(StatusKind kind) => Groups.SelectMany(g => g.Targets).Where(t => t.Kind == kind);

    // The Cue List, as the Script (XRV-SCR-V1.0) and the Cue List (XRV-CLS-V1.0) give it: CueListData is an array of {"Cue": [ {RealCue | VirtualCue | MixedCue} ]}.
    public static IReadOnlyList<Cue> ReadAll(JsonNode? cueListData)
    {
        var cues = new List<Cue>();
        foreach (var item in (cueListData as JsonArray) ?? [])
            foreach (var entry in (JsonArray)item!["Cue"]!)
            {
                var o = (JsonObject)entry!;
                if (o["RealCue"] is JsonObject real) cues.Add(Make(real["RECueID"]!.GetValue<string>(), CueKind.Real, real["REGroups"]));
                else if (o["VirtualCue"] is JsonObject virt) cues.Add(Make(virt["VECueID"]!.GetValue<string>(), CueKind.Virtual, virt["VEGroups"]));
                else if (o["MixedCue"] is JsonObject mixed) cues.Add(Make(mixed["MECueID"]!.GetValue<string>(), CueKind.Mixed, mixed["MEGroups"]));
            }
        return cues;
    }

    private static Cue Make(string id, CueKind kind, JsonNode? groups) =>
        new(id, kind, ((JsonArray?)groups ?? []).Select(g => Group(kind, (JsonObject)g!)).ToList());

    private static CueGroup Group(CueKind kind, JsonObject g)
    {
        var targets = new List<Target>();
        var descriptors = false;
        foreach (var conditions in new[] { g["RECueConditions"], g["VECueConditions"] })
        {
            if (conditions is not JsonObject c) continue;
            foreach (var info in KindInfo.All)
            {
                if (c[info.StatusKey] is JsonObject status) targets.Add(new Target(info.Kind, status));
                if (c[info.DescriptorsKey] is not null) descriptors = true;
            }
        }
        List<string> Ids(string key) => ((JsonArray?)g[key] ?? []).Select(x => x!.GetValue<string>()).ToList();
        return new CueGroup(kind, targets, descriptors, Ids("REActions"), Ids("VEActions"), g["NextCue"]?.GetValue<string>());
    }
}

public sealed class Script(JsonObject json) : XrvData(json)
{
    public override string Header => Types.Script;
    public override string SchemaFile => "XRV1/V1.0/data/Script.json";
    public string Id => Json["ScriptID"]!.GetValue<string>();

    // The Cues, in the order of the Cue List.
    public IReadOnlyList<Cue> Cues => _cues ??= Cue.ReadAll(Json["CueList"]);
    private IReadOnlyList<Cue>? _cues;

    public Cue? Find(string cueId) => Cues.FirstOrDefault(c => c.Id == cueId);

    // THE SHOW HAS AN OPERATOR when the Script lists at least one operator console; a show with no operator runs from the first to the last Cue.
    public bool HasOperator => (Json["Operators"] as JsonArray)?.Any(o => o?["Consoles"] is JsonArray { Count: > 0 }) ?? false;

    public static Script Read(string json) => Read(json, j => new Script(j));
}

public sealed class CueList(JsonObject json) : XrvData(json)
{
    public override string Header => "XRV-CLS-V1.0";
    public override string SchemaFile => "XRV1/V1.0/data/CueList.json";
    public IReadOnlyList<Cue> Cues => Cue.ReadAll(Json["CueListData"]);
    public static CueList Read(string json) => Read(json, j => new CueList(j));
}
