using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Metadata;
using AIF.Trust;
using Json.Schema;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 1 (M3223): MPAI-PTF V1.0 as written, tested. Its schemas as they
// are - what each identifies itself by, what it requires and does not define - its
// canonical form, and the signature of a PTF object. What PTF lacks is recorded in
// the expected files: they are the findings reported for PTF.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class PtfTests
{
    private static string Schemas => Path.Combine(Repository.Root, "schemas");
    private static string PtfData => Path.Combine(Schemas, "PTF", "V1.0", "data");

    // THE SCHEMAS. For each: its Header, whether a well-formed Header value matches
    // it, the required members it does not define (and whether that makes every
    // instance invalid), and the case of its member names.
    [Fact]
    public void Schemas_AsWritten()
    {
        var result = new Dictionary<string, string>();
        var names = new Dictionary<string, int> { ["UpperCamelCase"] = 0, ["lowerCamelCase"] = 0 };
        foreach (var file in Directory.EnumerateFiles(PtfData, "*.json").Order())
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var schema = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var built = PublishedSchemas.At(Schemas).ContainsKey(Path.GetFullPath(file));
            result[$"{name}: built"] = built ? "yes" : "no";

            var header = schema["properties"]?["Header"];
            if (header is null) result[$"{name}: Header"] = "none";
            else if (header["const"] is { } c) result[$"{name}: Header"] = "const " + c;
            else if ((string?)header["pattern"] is { } p)
            {
                // The identifier the pattern is for, as its prefix names it: STD-XXX.
                var prefix = Regex.Match(p, @"[A-Z]{3}-[A-Z]{3}").Value;
                var wellFormed = $"{prefix}-V1.0";
                result[$"{name}: Header"] = $"pattern {p}; {wellFormed} " + (Regex.IsMatch(wellFormed, p) ? "matches" : "does not match");
            }
            else result[$"{name}: Header"] = "a string, unconstrained";

            var undefined = new List<string>();
            var top = schema["properties"] as JsonObject;
            Walk(schema, "", (node, path) =>
            {
                if (node["required"] is not JsonArray required) return;
                var props = node["properties"] as JsonObject ?? (path.Contains("/oneOf/") || path.Contains("/anyOf/") ? new JsonObject() : null);
                if (props is null) return;
                var closed = node["additionalProperties"] is JsonValue v && v.GetValueKind() == JsonValueKind.False;
                // In an alternative (oneOf, anyOf), a member the top level defines is defined.
                var alternative = path.Contains("/oneOf/") || path.Contains("/anyOf/");
                foreach (var r in required.Select(x => (string?)x).Where(r => r is not null && !props.ContainsKey(r!) && !(alternative && top?.ContainsKey(r!) == true)))
                    undefined.Add($"{(path.Length == 0 ? "/" : path)} {r}" + (closed ? " (no instance can validate)" : " (left unconstrained)"));
            });
            if (undefined.Count > 0) result[$"{name}: required and not defined"] = string.Join("; ", undefined);

            Walk(schema, "", (node, _) =>
            {
                if (node["properties"] is not JsonObject props) return;
                foreach (var key in props.Select(p => p.Key).Where(k => k.Length > 0 && char.IsLetter(k[0])))
                    names[char.IsUpper(key[0]) ? "UpperCamelCase" : "lowerCamelCase"]++;
            });
        }
        result["member names"] = $"{names["UpperCamelCase"]} UpperCamelCase, {names["lowerCamelCase"]} lowerCamelCase (Data Conventions: lowerCamelCase)";
        Expected.Match("ptf-schemas.json", result);
    }

    private static void Walk(JsonNode? node, string path, Action<JsonObject, string> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                visit(obj, path);
                foreach (var (k, v) in obj) Walk(v, path + "/" + k, visit);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) Walk(array[i], path + "/" + i, visit);
                break;
        }
    }

    // PTF-JSON-CANON-V1: the canonical text of a few objects, chosen for the rules.
    [Fact]
    public void Canonical_Form()
    {
        var result = new Dictionary<string, string>();
        string C(string json) => PtfCanonical.Text(JsonNode.Parse(json));

        result["members ordered, at every depth"] = C("""{ "b": 2, "a": { "d": [3, 1], "c": "x" } }""");
        result["no whitespace, array order kept"] = C("""[ 3 , 1 , { "z" : 1 , "y" : [ 2 , 1 ] } ]""");
        result["strings escaped only where JSON requires"] = C(@"{ ""s"": ""é \""q\"" \\ \n \u0001 / \u2028"" }");
        result["numbers"] = C("""[ 1.0, -0.5, 100, 1E+21, 0.000001, 12.50, -0 ]""");
        result["code points, not UTF-16 units"] = C(@"{ ""\uD83D\uDE00"": 1, ""\uFFFF"": 2 }");

        var sample = """{ "Header": "PTF-MSG-V1.0", "MessageID": "m1", "Request": { "TargetID": "b", "Operation": "o" }, "n": 2.5 }""";
        var once = C(sample);
        result["canonical form of the canonical form"] = C(once) == once ? "the same" : "different";
        result["no byte-order mark"] = PtfCanonical.Bytes(JsonNode.Parse(sample))[0] == 0xEF ? "present" : "absent";
        Expected.Match("ptf-canonical.json", result);
    }

    // THE SIGNATURE of a PTF object, as the Signature Conventions have it: the
    // signature value in hexadecimal, and the KeyID of the key that verifies it,
    // over the canonical form without the Signature. Any change detected; a change
    // of order or whitespace not.
    [Fact]
    public void Signature()
    {
        var result = new Dictionary<string, string>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var publicKey = ECDsa.Create(key.ExportParameters(false));
        using var otherPublic = ECDsa.Create(other.ExportParameters(false));
        ECDsa? KeyFor(string id) => id switch { "key-A" => publicKey, "key-B" => otherPublic, _ => null };

        var message = TrustRequest();
        PtfSignature.Sign(message, key, "key-A");
        result["signed, verified"] = PtfSignature.Verify(message, KeyFor).ToString();
        result["the Signature"] = $"{((string)message["Signature"]!).Length} hexadecimal characters";
        result["the KeyID"] = (string)message["KeyID"]!;

        var reordered = JsonNode.Parse(Reorder(message).ToJsonString(new JsonSerializerOptions { WriteIndented = true }))!.AsObject();
        result["members reordered, whitespace added"] = PtfSignature.Verify(reordered, KeyFor).ToString();

        var changed = message.DeepClone().AsObject();
        changed["Request"]!["TargetID"] = "PI-C";
        result["a nested value changed"] = PtfSignature.Verify(changed, KeyFor).ToString();

        var added = message.DeepClone().AsObject();
        added["DescrMetadata"] = "added";
        result["a member added"] = PtfSignature.Verify(added, KeyFor).ToString();

        var value = message.DeepClone().AsObject();
        var v = (string)value["Signature"]!;
        value["Signature"] = (v[0] == 'A' ? "B" : "A") + v[1..];
        result["the Signature changed"] = PtfSignature.Verify(value, KeyFor).ToString();

        var otherKey = message.DeepClone().AsObject();
        otherKey["KeyID"] = "key-B";
        result["the KeyID of another known key"] = PtfSignature.Verify(otherKey, KeyFor).ToString();

        var unknown = message.DeepClone().AsObject();
        unknown["KeyID"] = "key-Z";
        result["the KeyID of a key not known"] = PtfSignature.Verify(unknown, KeyFor).ToString();

        var removed = message.DeepClone().AsObject();
        removed.Remove("Signature");
        result["no Signature"] = PtfSignature.Verify(removed, KeyFor).ToString();

        // The signed message against its schema, as PTF requires of every structure.
        result["the signed TrustMessage against its schema"] = Violations("TrustMessage", message);
        var noKey = message.DeepClone().AsObject();
        noKey.Remove("KeyID");
        result["the same without its KeyID"] = Violations("TrustMessage", noKey);
        Expected.Match("ptf-signature.json", result);
    }

    // A TrustRequest as the TrustMessage schema has it: PI-A asks whether PI-B may
    // receive dataset X (End-to-End Examples 1).
    private static JsonObject TrustRequest() => new()
    {
        ["Header"] = "PTF-MSG-V1.0",
        ["MessageID"] = "msg-0001",
        ["MessageTime"] = new JsonObject { ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = "t-0001", ["Data"] = "2026-09-25T22:00:00Z" },
        ["MessageType"] = "TrustRequest",
        ["RequesterID"] = "PI-A",
        ["ResponderID"] = "PI-B",
        ["Request"] = new JsonObject { ["Operation"] = "ReceiveDatasetX", ["TargetType"] = "ProcessInstance", ["TargetID"] = "PI-B" }
    };

    private static JsonObject Reorder(JsonObject obj)
    {
        var copy = new JsonObject();
        foreach (var (k, v) in obj.Reverse()) copy[k] = v is JsonObject o ? Reorder(o) : v?.DeepClone();
        return copy;
    }

    private static string Violations(string schemaName, JsonObject instance)
    {
        var schema = PublishedSchemas.At(Schemas)[Path.GetFullPath(Path.Combine(PtfData, schemaName + ".json"))];
        EvaluationResults evaluation;
        using var document = JsonDocument.Parse(instance.ToJsonString());
        lock (PublishedSchemas.Lock)
            evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid) return "valid";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in evaluation.Details ?? [])
            if (d.Errors is { Count: > 0 })
                foreach (var (k, m) in d.Errors)
                    found.Add($"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l : "/")} {k}: {(m.Length > 90 ? m[..90] + "..." : m)}");
        return string.Join("; ", found);
    }
}
