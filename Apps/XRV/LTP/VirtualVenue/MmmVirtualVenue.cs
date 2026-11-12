using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using Mpai.Mmm;
using Mpai.Mmm.Client;
using Mpai.Ua;

namespace Mpai.Xrv.VirtualVenue;

// THE VIRTUAL VENUE OF XRV-LTP, ON MMM-TEC V2.2. The virtual stage is an M-Location of an M-Instance; the show is a collection of Processes in it - the Users of the performers and of the participants, and whatever animates Items. This device is one of them: the Process that
// animates the Items of the virtual venue. It does what it does only by
// Process Actions over the MMM-API (MmmClient), with the token the M-Instance gave it:
//   - the Virtual Objects of the VE Venue Specification (OSD-3DO-V1.5) are placed on the stage with MM-Add, once, before anything else;
//   - VE Data Out (XRV-VDO-V1.0), which the Module delivers: each Metaverse Object carries a command of the Action Table {Object, Property, Value}; a Property that MMM-TEC
//     Property Change (MMM-PCP-V2.2) can change is changed with it - Intensity, as the Light Source of the object (RQ7, CVE-LTS-V1.0). A command MMM-TEC cannot perform is not
//     performed, and said (Unperformed);
//   - VE Data In (XRV-VDI-V1.0), which the device gives: what MM-Capture of the stage sees - each perceptible Thing on it that is not this Process's own, when it comes onto the
//     stage, as a Metaverse Object with its ID.
public sealed class MmmVirtualVenue(MmmClient venue, string stage, JsonObject veVenueSpecification, TimeSpan? captureEvery = null) : IDevice
{
    public const string VDI = "XRV-VDI-V1.0", VDO = "XRV-VDO-V1.0";

    private readonly SemaphoreSlim placing = new(1, 1);
    private bool placed;
    private long count;
    private readonly List<string> unperformed = new();
    private readonly string? environment = veVenueSpecification["UEnvironmentID"]?.GetValue<string>();
    private readonly string? instance = veVenueSpecification["MInstanceID"]?.GetValue<string>();

    // What could not be done on the stage, and why.
    public IReadOnlyList<string> Unperformed { get { lock (unperformed) return unperformed.ToList(); } }
    private void Say(string why) { lock (unperformed) unperformed.Add(why); }

    // The Virtual Objects of the VE Venue Specification, placed on the stage with MM-Add.
    private async Task PlaceAsync()
    {
        await placing.WaitAsync();
        try
        {
            if (placed) return;
            foreach (var o in veVenueSpecification["VirtualObjects"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (o["3DModelObjectID"]?.GetValue<string>() is not { } id) continue;
                var rs = await venue.PerformAsync("MM-Add", new Complements { Nil = id, At = stage, With = [o.DeepClone()] });
                if (rs.Http != 200) Say($"MM-Add {id} at {stage}: {rs.Http} {rs.Response.PaStatus.Code} {rs.Response.PaStatus.Detail}");
            }
            placed = true;
        }
        finally { placing.Release(); }
    }

    public async Task DeliverAsync(string dataType, string json, CancellationToken cancel)
    {
        if (dataType != VDO) return;
        await PlaceAsync();
        foreach (var m in JsonNode.Parse(json)?["VEDataOut"]?["MetaverseObjects"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var d in m["MetaverseObject"]?["MetaverseObjectData"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (d["Data"]?.GetValue<string>() is not { } text || JsonNode.Parse(text) is not JsonObject command) continue;
                var target = command["Object"]?.GetValue<string>();
                var property = command["Property"]?.GetValue<string>();
                if (target is null || property is null) { Say($"a command with no Object or no Property: {text}"); continue; }
                if (property != "Intensity") { Say($"{target}.{property}: not a Property that MMM-TEC Property Change changes"); continue; }
                var rs = await venue.PerformAsync("Property Change", new Complements { Nil = target, With = [LightSource(target, command["Value"]!.GetValue<double>())] });
                if (rs.Http != 200) Say($"Property Change {target}.{property}: {rs.Http} {rs.Response.PaStatus.Code} {rs.Response.PaStatus.Detail}");
            }
    }

    // A Light Source (CVE-LTS-V1.0) of the object: a point light where the object is, white, of the Intensity the command gives.
    private JsonObject LightSource(string target, double intensity)
    {
        var light = new JsonObject
        {
            ["Header"] = "CVE-LTS-V1.0", ["LightSourceID"] = target,
            ["LightSourceSpaceTime"] = new JsonObject { ["Header"] = "OSD-SPT-V1.5", ["SpaceTimeID"] = target + "-st" },
            ["LightSourceType"] = new JsonArray(new JsonObject { ["Point"] = new JsonObject { ["Header"] = "OSD-OPS-V1.5", ["PositionID"] = target + "-p", ["CartPosition"] = new JsonArray(0.0, 0.0, 0.0) } }),
            ["ColourInformation"] = new JsonObject { ["ColourData"] = "FFFFFF", ["ColourSpace"] = new JsonObject { ["Header"] = "TFA-CLQ-V1.5", ["ColourQualifierID"] = target + "-c", ["Formats"] = "sRGB" } },
            ["Intensity"] = intensity
        };
        if (instance is not null) light["MInstanceID"] = instance;
        return light;
    }

    // What MM-Capture of the stage sees: a Thing that comes onto it is given as VE Data In; one that leaves and comes back, again.
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await PlaceAsync();
        var every = captureEvery ?? TimeSpan.FromMilliseconds(200);
        var present = new HashSet<string>(StringComparer.Ordinal);
        while (!cancel.IsCancellationRequested)
        {
            var rs = await venue.PerformAsync("MM-Capture", new Complements { At = stage });
            if (rs.Http == 200 && rs.Response.Complements?.With?.FirstOrDefault() is JsonObject scene)
            {
                var things = scene["ItemValueOrRef"]?[0]?["Things"]?.AsArray().OfType<JsonObject>()
                             .Where(t => (string?)t["ID"] != stage && (string?)t["Owner"] != venue.ProcessID && (string?)t["Kind"] != "Location")
                             .Select(t => t["ID"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
                var arrived = things.Where(t => !present.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
                present.IntersectWith(things);
                present.UnionWith(arrived);
                if (arrived.Count > 0) yield return (VDI, DataIn(arrived).ToJsonString());
            }
            else Say($"MM-Capture {stage}: {rs.Http} {rs.Response.PaStatus.Code} {rs.Response.PaStatus.Detail}");
            try { await Task.Delay(every, cancel); } catch (OperationCanceledException) { yield break; }
        }
    }

    private JsonObject DataIn(IEnumerable<string> ids)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = $"mmm-vdi-{Interlocked.Increment(ref count)}";
        return new JsonObject
        {
            ["Header"] = VDI, ["VEDataInID"] = id, ["UEnvironmentID"] = environment ?? "", ["VEDataInTime"] = Simple(id + "-t", now),
            ["VEDataIn"] = new JsonObject
            {
                ["MetaverseObjects"] = new JsonArray(ids.Select(t => (JsonNode)new JsonObject
                {
                    ["MetaverseObject"] = new JsonObject
                    {
                        ["Header"] = "XRV-MVO-V1.0", ["MetaverseObjectID"] = t, ["UEnvironmentID"] = environment ?? "", ["MetaverseObjectTime"] = Simple(t + "-t", now),
                        ["MetaverseObjectData"] = new JsonArray(new JsonObject { ["ID"] = t }),
                        ["MetaverseQualifier"] = new JsonObject { ["Header"] = "TFA-MAQ-V1.5", ["MetaverseQualifierID"] = t + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() }
                    }
                }).ToArray())
            }
        };
    }

    // A Simple Time (OSD-STM-V1.5): one absolute time, in milliseconds.
    private static JsonObject Simple(string id, double ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0, ["TimeType"] = true, ["TimeUnit"] = "01" })
    };

    // The stage stays as it is: nothing on it moves by itself.
    public Task SafeStopAsync() => Task.CompletedTask;
}
