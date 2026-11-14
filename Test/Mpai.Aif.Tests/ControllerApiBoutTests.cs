using System.Text.Json.Nodes;
using AIF.Bout;
using AIF.Controller;
using Mpai.Aif.Api;
using Xunit;

namespace Mpai.Aif.Tests;

// BOUTS AT THE BOUNDARY OF A MODULE, through the Controller API called by the User Agent (MPAI-AIF V3.0, Basic API 3.1.3 and 3.10). The Module is the
// echo of the storage tests (TST-TRE inside), with its Port 1 declared "Bout": true in its Metadata and its Port 2 not.
[Collection(Timing.Name)]
public sealed class ControllerApiBoutTests
{
    private const string Text = StorageTests.Text;
    private const string Module = "1TST-BOU-V1.0-I01";

    private static ControllerApi Api()
    {
        var amds = Path.Combine(Path.GetTempPath(), "mpai-bout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(StorageTests.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(amds, "1TST-RCA-V1.0-I01.json")))!.AsObject();
        metadata["Identifier"]!["ImplementationID"] = "TST-BOU-V1.0-I01";
        metadata["Identifier"]!["AIMName"] = Module;
        metadata["Header"] = "TST-BOU-V1.0";
        metadata.Remove("Record");
        foreach (var port in metadata["ExternalPorts"]!.AsArray())
            if ((int)port!["PortNumber"]! == 1) port["Bout"] = true;
        File.WriteAllText(Path.Combine(amds, Module + ".json"), metadata.ToJsonString());
        return new ControllerApi(amds, Path.Combine(amds, "no-settings.json"), new StorageAims());
    }

    private static string Message(string text) => new JsonObject { ["Header"] = Text, ["Text"] = text }.ToJsonString();
    private static string? TextOf(string? json) => json is null ? null : (string?)JsonNode.Parse(json)?["Text"];

    [Fact]
    public void OutputBout_TheControllerGivesFramesTheUserAgentVerifies()
    {
        using var api = Api();
        Assert.Equal(AifError.OK, api.StartFlow(Module, out var moduleId));
        Assert.Equal(AifError.NotAuthorised, api.BoutOpen(moduleId, Text, 2, BoutSecurity.EachInstance, 1, out _));
        Assert.Equal(AifError.OK, api.BoutOpen(moduleId, Text, 1, BoutSecurity.EachInstance, 1, out var boutId));

        // The first Message makes the Bout and is read whole.
        Assert.Equal(AifError.OK, api.InputWrite(moduleId, Text, 1, Message("first"), 5000));
        var first = api.BoutOutputRead(moduleId, Text, 1, 5000);
        Assert.Equal("first", TextOf(first.Json));
        Assert.Equal(AifError.OK, api.BoutGet(moduleId, Text, 1, out var bout));
        Assert.Equal(boutId, (int)bout!["BoutID"]!);

        var anchorKey = BoutKey.FromSpki(Convert.FromHexString((string)api.BoutAnchor()["PublicKey"]!["KeyValue"]!))!;
        var keyId = (string)api.BoutAnchor()["AnchorID"]!;
        var receiver = new BoutReceiver(new BoutReceiver.Options { DataType = Text, PortNumber = 1, KeyFor = id => id == keyId ? anchorKey : null });
        Assert.True(receiver.Accept(bout).Ok);
        for (var k = 1; k <= 3; k++)
        {
            Assert.Equal(AifError.OK, api.InputWrite(moduleId, Text, 1, Message($"frame {k}"), 5000));
            var read = api.BoutOutputRead(moduleId, Text, 1, 5000);
            Assert.NotNull(read.Frame);
            var got = receiver.Receive(read.Frame!);
            Assert.True(got.Ok, got.Reason);
            Assert.Equal($"frame {k}", (string?)Assert.Single(got.Delivered)["Text"]);
        }
        Assert.Equal(AifError.OK, api.BoutClose(moduleId, Text, 1, out var closing));
        Assert.True(receiver.Close(closing!).Ok);
        api.StopFlow(moduleId);
    }

    [Fact]
    public void InputBout_TheUserAgentGivesABoutWithItsCredential()
    {
        using var api = Api();
        Assert.Equal(AifError.OK, api.StartFlow(Module, out var moduleId));
        var key = BoutKey.Create();
        var now = DateTimeOffset.UtcNow;
        var giver = new BoutGiver(new BoutGiver.Options
        {
            DataType = Text, PortNumber = 1, BoutId = 70001, SigningMode = BoutSecurity.Chain, ChainInterval = 2,
            Key = key, KeyId = "UA-1", Issuer = "urn:mpai:ua:UA-1", SourceAimId = "UA-1"
        });
        var bout = giver.Open(JsonNode.Parse(Message("first"))!.AsObject());

        // Without a credential the Bout is refused; with one, accepted.
        Assert.Equal(AifError.NotTrusted, api.BoutGive(moduleId, Text, 1, bout));
        Assert.Equal(AifError.OK, api.CredentialObtain(BoutCredentials.Proof(key, "UA-1", now), out var credential));
        Assert.Equal("ProcessInstance", (string?)credential!["Subject"]!["InstanceType"]);
        Assert.Equal(AifError.NotAuthorised, api.BoutGive(moduleId, Text, 2, bout));
        Assert.Equal(AifError.OK, api.BoutGive(moduleId, Text, 1, bout));

        // A Chain of two: the AIMs read the first only when the second has verified it.
        Assert.Equal(AifError.OK, api.BoutInputWrite(moduleId, Text, 1, giver.Instance(JsonNode.Parse(Message("one"))!.AsObject()), 5000));
        Assert.Equal(AifError.NotProduced, api.OutputRead(moduleId, Text, 1, 300).Error);
        Assert.Equal(AifError.OK, api.BoutInputWrite(moduleId, Text, 1, giver.Instance(JsonNode.Parse(Message("two"))!.AsObject()), 5000));
        Assert.Equal("one", TextOf(api.OutputRead(moduleId, Text, 1, 5000).Json));
        Assert.Equal("two", TextOf(api.OutputRead(moduleId, Text, 1, 5000).Json));

        // A tampered frame is refused.
        var frame = giver.Instance(JsonNode.Parse(Message("three"))!.AsObject(), last: true);
        frame[^3] ^= 0x55;
        Assert.NotEqual(AifError.OK, api.BoutInputWrite(moduleId, Text, 1, frame, 5000));
        Assert.Equal(AifError.Failed, api.BoutEnd(moduleId, Text, 1, giver.Close()));   // CloseBout reports the instance that did not arrive
        api.StopFlow(moduleId);
    }
}
