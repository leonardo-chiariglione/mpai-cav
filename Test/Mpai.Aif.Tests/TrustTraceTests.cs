using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Metadata;
using AIF.Trust;
using Json.Schema;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// PHASE 13, STEP 7 (M3223 3.6): the Trace of the Controller's trust decisions. Each
// Trust Operation is a record of an AIF Trace, signed by the Controller, carrying
// its sequence and the hash of the record before it - of the Controller's Trust
// Anchor object, for the first. The chain verified; a record removed, reordered,
// inserted, changed, or cut from the end, detected; a Controller started again on
// its Trace continues it, and does not start on a Trace that is broken.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class TrustTraceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Chain()
    {
        var result = new Dictionary<string, string>();
        var file = Path.Combine(Path.GetTempPath(), $"mpai-phase13-trace-{Guid.NewGuid():N}.jsonl");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var anchor = new TrustAnchorKey("controller-1", key, T0.AddHours(-1), T0.AddDays(1));
        var now = T0;
        var trust = new TrustDomain(anchor, key, () => now += TimeSpan.FromSeconds(1), file);

        // Decisions of each kind: identity, lifecycle, policy, the pipeline.
        var aim = trust.IssueLocal("TST#1/1TST-UPP-V1.0-I01", "1TST-UPP-V1.0-I01", null);
        trust.BindPolicy(aim.Id, [("Port", "Input TST-TXT-V1.0#1")]);
        trust.Verify(aim, new VerificationPipeline.Expectation(aim.Id, "Created"));
        trust.Verify(aim, new VerificationPipeline.Expectation(aim.Id, "Running"));
        trust.Transition(aim.Id, "Running");

        var records = TrustTrace.Load(file);
        var anchorObject = anchor.Object();
        string Verified(IReadOnlyList<JsonObject> r, (long, string)? head = null) => TrustTrace.Verify(r, anchorObject, head) ?? $"intact, {r.Count} records";
        result["the Trace, as kept"] = Verified(records);
        result["its records"] = string.Join(", ", records.Select(r => $"{r["Sequence"]} {r["TrustOperation"]!["OperationType"]} {r["TrustOperation"]!["Status"]}"));
        result["its records against AIF-TRC"] = string.Join("; ", records.Select(r => Violations(Path.Combine("AIF", "V3.0", "data", "Trace.json"), r)).Distinct());
        result["their Trust Operations against PTF-TOP"] = string.Join("; ", records.Select(r => Violations(Path.Combine("PTF", "V1.0", "data", "TrustOperation.json"), r["TrustOperation"]!.AsObject())).Distinct());
        result["the first begins at the anchor"] = (string?)records[0]["Previous"]!["Hash"] == TrustTrace.Genesis(anchorObject) ? "yes" : "no";
        result["kept in memory, and in its file"] = trust.Trace.Records.Select(r => r.ToJsonString()).SequenceEqual(records.Select(r => r.ToJsonString())) ? "the same" : "not the same";

        // TAMPERED.
        List<JsonObject> Copy() => records.Select(r => r.DeepClone().AsObject()).ToList();
        var removed = Copy(); removed.RemoveAt(3);
        result["a record removed"] = Verified(removed);
        var swapped = Copy(); (swapped[2], swapped[3]) = (swapped[3], swapped[2]);
        result["two records swapped"] = Verified(swapped);
        var failed = records.FindIndex(r => (string?)r["TrustOperation"]!["Status"] == "Failure");
        var changed = Copy(); changed[failed]["TrustOperation"]!["Status"] = "Success";
        result["a failure changed to a success"] = Verified(changed);
        var failure = Copy(); failure[failed]["TrustOperation"]!.AsObject().Remove("FailureReason");
        result["a failure's reason removed"] = Verified(failure);

        // A record made by another: signed by another key, however well chained.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var inserted = Copy();
        var forged = inserted[2].DeepClone().AsObject();
        forged["Previous"]!["Hash"] = TrustTrace.HashOf(inserted[2]);
        PtfSignature.Sign(forged, other, "controller-1");
        inserted.Insert(3, forged);
        result["a record inserted, signed by another key"] = Verified(inserted);

        var head = trust.Trace.Head;
        var cut = Copy(); cut.RemoveAt(cut.Count - 1);
        result["the last record cut, without the head"] = Verified(cut);
        result["the last record cut, against the head"] = Verified(cut, head);
        result["as kept, against the head"] = Verified(records, head);

        // STARTED AGAIN on its Trace: it continues the chain.
        var again = new TrustDomain(anchor, key, () => now += TimeSpan.FromSeconds(1), file);
        again.IssueLocal("TST#2/1TST-SLP-V1.0-I01", "1TST-SLP-V1.0-I01", null);
        var continued = TrustTrace.Load(file);
        result["started again, and a decision made"] = Verified(continued, again.Trace.Head);

        // Started on a Trace that is broken: it does not start.
        File.WriteAllLines(file, removed.Select(r => r.ToJsonString()));
        try { _ = new TrustDomain(anchor, key, () => now, file); result["started on a broken Trace"] = "started"; }
        catch (InvalidDataException e) { result["started on a broken Trace"] = "refused: " + e.Message.Replace(file, "<file>"); }
        Expected.Match("trust-trace.json", result);
    }

    // A MODULE, with AIMs on a host, under trust: every decision of the Controller in
    // its Trace - the host's link included - and the Trace intact.
    [Fact]
    public void Module()
    {
        var result = new Dictionary<string, string>();
        var file = Path.Combine(Path.GetTempPath(), $"mpai-phase13-trace-{Guid.NewGuid():N}.jsonl");
        var (anchor, key) = TrustAnchorKey.Load(TrustedParties.ControllerFile);
        var amds = TrustEvidenceTests.ApprovedCopy();
        using (var host = TrustedParties.Host())
        {
            using var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), new RemoteAims());
            api.Controller.Trust = new TrustDomain(anchor, key, null, file);
            api.Controller.HostAnchors.Add(host.Anchor!);
            foreach (var aim in RemoteTests.Hosted["TST-RXC"]) api.Controller.AimHosts[aim] = host.Address;
            var name = "1TST-RXC-V1.0-I01";
            result["TST-RXC started"] = api.StartFlow(name).ToString();
            api.Advance(name, [new ControllerApi.Datum(RemoteTests.Text, "x")]);
            api.StopFlow(name);
        }
        using (var host = TrustedParties.Host(controllers: TrustedParties.AnchorOf(TrustedParties.Save("controller-2"))))
        {
            using var api = new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), new RemoteAims());
            api.Controller.Trust = new TrustDomain(anchor, key, null, file);           // the same Controller, started again
            api.Controller.HostAnchors.Add(host.Anchor!);
            foreach (var aim in RemoteTests.Hosted["TST-RXC"]) api.Controller.AimHosts[aim] = host.Address;
            result["TST-RXC, on a host that does not trust this Controller"] = api.StartFlow("1TST-RXC-V1.0-I01").ToString();
        }
        var records = TrustTrace.Load(file);
        result["the Trace"] = TrustTrace.Verify(records, anchor.Object()) ?? "intact";
        result["its decisions"] = string.Join(", ", records.Select(r => r["TrustOperation"]!)
            .GroupBy(o => $"{o["OperationType"]} {o["TargetType"]} {o["Status"]}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));
        result["the refusal, as traced"] = string.Join("; ", records.Select(r => r["TrustOperation"]!)
            .Where(o => (string?)o["Status"] == "Failure").Select(o => $"{o["OperationType"]} {o["TargetType"]}: {o["FailureReason"]}"));
        Expected.Match("trust-trace-module.json", result);
    }

    private static string Violations(string relative, JsonObject instance)
    {
        var schemas = Path.Combine(Repository.Root, "schemas");
        var schema = PublishedSchemas.At(schemas)[Path.GetFullPath(Path.Combine(schemas, relative))];
        EvaluationResults evaluation;
        using var document = JsonDocument.Parse(instance.ToJsonString());
        lock (PublishedSchemas.Lock)
            evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (evaluation.IsValid) return "valid";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in evaluation.Details ?? [])
            if (d.Errors is { Count: > 0 })
                foreach (var (k, m) in d.Errors)
                    found.Add($"{(d.InstanceLocation.ToString() is { Length: > 0 } l ? l : "/")} {k}: {Regex.Replace(m.Length > 90 ? m[..90] + "..." : m, @"\s+", " ")}");
        return string.Join("; ", found);
    }
}
