using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIF.Metadata;

// AN L3 IS AN INSTANCE OF ITS L2. The schema checks the form of one file; this
// checks that an Implementation's L3 is what the Standard's L2 allows. The L2 is
// the one the L3's Header names.
//
// An L3 declares only what its Implementation supports. So:
//   1. every Port of the L3 is a Port of the L2: the same Direction, and the L2's
//      Data Type(s) include the L3's (an L2 Port may accept a Basic and a Full
//      Object, an L3 Port one of them);
//   2. an Input both declare is optional in both, or in neither; an Output the L2
//      requires is not optional in the L3 (an L3 may always produce an Output the
//      L2 makes optional: it gives more, not less);
//   3. a Port of the L2 that the L3 does not declare is optional in the L2;
//   4. every Sub-AIM of a composite L3 is a Sub-AIM of its L2, or a composite whose
//      Sub-AIMs include one, at any depth - a Module may use a composite AIM where
//      its Standard names one of the AIMs that composite contains -
//   5. or a combination of Sub-AIMs of the L2 that exposes their interface (below).
// Ports are matched by Direction and Data Type; names are labels, but where two
// Ports could match, the one of the same name is preferred.
public sealed class L2Conformance
{
    private readonly Dictionary<string, JsonElement> l2s = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, JsonElement?> findL3;

    // schemasRoot: the folder holding */V*/AIMs/*.json (the L2s).
    // findL3: an L3 by its AIM Instance identifier, to look inside a composite Sub-AIM.
    public L2Conformance(string schemasRoot, Func<string, JsonElement?> findL3)
    {
        this.findL3 = findL3;
        foreach (var file in Directory.EnumerateFiles(schemasRoot, "*.json", SearchOption.AllDirectories)
                                      .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs"))
        {
            try
            {
                var l2 = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
                if (l2.TryGetProperty("Identifier", out var id) && id.TryGetProperty("AIMName", out var n) &&
                    n.GetString() is { Length: > 0 } name)
                    l2s[name] = l2;
            }
            catch { /* not an L2 */ }
        }
    }

    public int Count => l2s.Count;

    // The AIM Type an L3 implements: its Header, else its AIMName without the
    // ImplementerID and the Instance number.
    public static string TypeOf(JsonElement l3) =>
        l3.TryGetProperty("Header", out var h) && h.GetString() is { Length: > 0 } header
            ? header
            : TypeOf(l3.GetProperty("Identifier").GetProperty("AIMName").GetString() ?? "");

    public static string TypeOf(string instance) =>
        Regex.Replace(Regex.Replace(instance, @"^[0-9A-Za-z]*?(?=[A-Z]{3}-[A-Z]{3}-V)", ""), @"-I[0-9]+$", "");

    // What in the L3 its L2 does not allow; none when it conforms.
    public IReadOnlyList<string> Check(JsonElement l3)
    {
        var type = TypeOf(l3);
        if (!l2s.TryGetValue(type, out var l2))
            return [$"No L2 of {type}: an L3 is validated against its L2, and there is none."];

        var found = new List<string>();
        var p2 = Ports(l2); var p3 = Ports(l3);
        var used = new HashSet<int>();
        foreach (var x in p3)
        {
            var candidates = p2.Select((y, i) => (y, i))
                .Where(c => !used.Contains(c.i) && c.y.Direction == x.Direction && x.Types.IsSubsetOf(c.y.Types)).ToList();
            var pick = candidates.FirstOrDefault(c => c.y.Name == x.Name);
            if (pick.y is null) pick = candidates.FirstOrDefault();
            if (pick.y is null) { found.Add($"{x}: the L2 has no such Port."); continue; }
            used.Add(pick.i);
            var disagrees = x.Direction == "Output" ? x.Optional && !pick.y.Optional : pick.y.Optional != x.Optional;
            if (disagrees)
                found.Add($"{x}: {(x.Optional ? "optional" : "required")} here, {(pick.y.Optional ? "optional" : "required")} in the L2.");
        }
        for (var i = 0; i < p2.Count; i++)
            if (!used.Contains(i) && !p2[i].Optional)
                found.Add($"{p2[i]} of the L2 is not declared, and the L2 does not make it optional.");

        var subs2 = SubAims(l2).Select(TypeOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in SubAims(l3))
            if (!subs2.Contains(TypeOf(sub)) && !Contains(sub, subs2, 0) && !Combines(TypeOf(sub), l2, subs2))
                found.Add($"Sub-AIM {sub}: not a Sub-AIM of the L2, nor a composite containing one, nor a combination of Sub-AIMs of the L2 exposing their interface.");
        return found;
    }

    // 5. A COMBINATION. Two or more AIMs may be combined into one, provided that the
    //    result exposes the same interface. A Sub-AIM whose own L2 is a composite of
    //    Sub-AIMs of the parent's L2 conforms if its Ports are the interface that
    //    group exposes in the parent: the Topology lines of the parent that cross the
    //    group's border - in, its inputs; out, its outputs - each typed by the
    //    parent's own labels (its ExternalPorts and InternalTypes).
    private bool Combines(string type, JsonElement parent, HashSet<string> parentSubs)
    {
        if (!l2s.TryGetValue(type, out var combined)) return false;
        var group = SubAims(combined).Select(TypeOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (group.Count < 2 || !group.IsSubsetOf(parentSubs)) return false;

        var labels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in Ports(parent)) labels.TryAdd(p.Name, p.Types);
        if (parent.TryGetProperty("InternalTypes", out var its) && its.ValueKind == JsonValueKind.Array)
            foreach (var it in its.EnumerateArray())
                if (it.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    labels[name] = TypesOf(it);

        var exposed = new List<(string Direction, HashSet<string> Types)>();
        if (!parent.TryGetProperty("Topology", out var topology) || topology.ValueKind != JsonValueKind.Array) return false;
        foreach (var line in topology.EnumerateArray())
        {
            var (fromAim, fromLabel) = End(line, "Output");
            var (toAim, toLabel) = End(line, "Input");
            bool fromIn = group.Contains(TypeOf(fromAim)), toIn = group.Contains(TypeOf(toAim));
            if (fromIn == toIn) continue;                              // inside the group, or not touching it
            var types = labels.GetValueOrDefault(fromLabel) ?? labels.GetValueOrDefault(toLabel);
            if (types is null) return false;                           // a line the parent does not type
            var direction = toIn ? "Input" : "Output";
            if (!exposed.Any(e => e.Direction == direction && e.Types.SetEquals(types))) exposed.Add((direction, types));
        }

        var own = Ports(combined);
        bool Covered(string d, HashSet<string> t, IEnumerable<(string Direction, HashSet<string> Types)> by) =>
            by.Any(b => b.Direction == d && b.Types.Overlaps(t));
        return exposed.All(e => Covered(e.Direction, e.Types, own.Select(p => (p.Direction, p.Types)))) &&
               own.All(p => Covered(p.Direction, p.Types, exposed));
    }

    private static (string Aim, string Label) End(JsonElement line, string side)
    {
        if (!line.TryGetProperty(side, out var end)) return ("", "");
        return (end.TryGetProperty("AIMName", out var a) ? a.GetString() ?? "" : "",
                end.TryGetProperty("PortName", out var p) ? p.GetString() ?? "" : "");
    }

    private static HashSet<string> TypesOf(JsonElement x)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (x.TryGetProperty("DataType", out var t))
        {
            if (t.ValueKind == JsonValueKind.Array) foreach (var y in t.EnumerateArray()) types.Add(y.GetString() ?? "");
            else types.Add(t.GetString() ?? "");
        }
        return types;
    }

    private bool Contains(string instance, HashSet<string> types, int depth)
    {
        if (depth > 16 || findL3(instance) is not { } l3) return false;
        foreach (var sub in SubAims(l3))
            if (types.Contains(TypeOf(sub)) || Contains(sub, types, depth + 1)) return true;
        return false;
    }

    private static IEnumerable<string> SubAims(JsonElement aim)
    {
        if (!aim.TryGetProperty("SubAIMs", out var subs) || subs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var s in subs.EnumerateArray())
            if (s.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } name)
                yield return name;
    }

    private sealed record Port(string Direction, HashSet<string> Types, string Name, bool Optional)
    {
        public override string ToString() => $"{Direction} {string.Join("|", Types.Order())} ({Name})";
    }

    private static List<Port> Ports(JsonElement aim)
    {
        var ports = new List<Port>();
        if (!aim.TryGetProperty("ExternalPorts", out var array) || array.ValueKind != JsonValueKind.Array) return ports;
        foreach (var p in array.EnumerateArray())
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            if (p.TryGetProperty("DataType", out var t))
            {
                if (t.ValueKind == JsonValueKind.Array) foreach (var x in t.EnumerateArray()) types.Add(x.GetString() ?? "");
                else types.Add(t.GetString() ?? "");
            }
            ports.Add(new Port(
                p.TryGetProperty("Direction", out var d) ? d.GetString() ?? "" : "",
                types,
                p.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                p.TryGetProperty("IsOptional", out var o) && o.ValueKind == JsonValueKind.True));
        }
        return ports;
    }
}
