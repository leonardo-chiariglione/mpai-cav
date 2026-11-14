using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE VOICE COMMAND INTERFACE (MMC-VCI-V2.5), a Composite of the Automatic Speech Recognition, the JSON Interface, Entity Dialogue Processing and Text to Speech. Judged:
//   - its L3 is its L2's Topology with the instances named, nothing more: the same connections, by Data Type and Port Number;
//   - the L3 of the JSON Interface for an application and the neutral one are instances of the same L2: what is of one application is in the Access;
//   - run by the Controller with the speech faked and the language model answered by the test: what is heard leaves as the Heard Text and becomes a command at the Application
//     Command Port, which is not said; the response of the application, at the Application Response Port, is said (Reply Text and Reply Speech); a request that gives no
//     command is answered at once with what is missing, and nothing leaves at the Application Command Port;
//   - the dialogue is told the facts, not the JSON: nothing of the application is in what Entity Dialogue Processing is given.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class VciTests
{
    private static JsonElement L3(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, name + ".json"))).RootElement.Clone();
    private static JsonElement L2() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "MMC", "V2.5", "AIMs", "VoiceCommandInterface.json"))).RootElement.Clone();

    private static string End(JsonElement e) =>
        $"{(e.TryGetProperty("AIMName", out var n) ? n.GetString() : "")}|{e.GetProperty("DataType").GetString()}|{(e.TryGetProperty("PortNumber", out var p) ? p.GetInt32() : 1)}";

    private static IEnumerable<string> Connections(JsonElement aim, Func<string, string> aimName) =>
        aim.GetProperty("Topology").EnumerateArray()
           .Select(c => $"{Name(c.GetProperty("Output"), aimName)} -> {Name(c.GetProperty("Input"), aimName)}")
           .Order(StringComparer.Ordinal);

    private static string Name(JsonElement end, Func<string, string> aimName)
    {
        var name = end.TryGetProperty("AIMName", out var n) ? n.GetString() ?? "" : "";
        return $"{(name.Length == 0 ? "" : aimName(name))}|{end.GetProperty("DataType").GetString()}|{(end.TryGetProperty("PortNumber", out var p) ? p.GetInt32() : 1)}";
    }

    [Fact]
    public void ItsL3IsItsL2sTopologyWithTheInstancesNamed()
    {
        var l2 = L2(); var l3 = L3("1MMC-VCI-V2.5-I01");
        var instances = l3.GetProperty("SubAIMs").EnumerateArray().Select(s => s.GetProperty("Identifier").GetProperty("AIMName").GetString()!).ToList();
        string Instance(string l2Name) => instances.Single(i => i.StartsWith("1" + l2Name + "-I", StringComparison.Ordinal));

        // The L2 names four AIMs; the L3 names an instance of each, and no other.
        Assert.Equal(4, instances.Count);
        // The L3 may leave out what its Implementation does not support: the Language Selector, which the Automatic Speech Recognition of this L3 (1MMC-ASR-V2.5-I01) has no Port for.
        var expected = Connections(l2, n => Instance(n)).Where(c => !c.Contains("OSD-SEL-V1.5")).ToList();
        Assert.Equal(expected, Connections(l3, n => n));
        // The Ports of the boundary are those of the L2, by Data Type and Port Number.
        string Ports(JsonElement a) => string.Join(" ", a.GetProperty("ExternalPorts").EnumerateArray().Select(p => $"{p.GetProperty("Direction").GetString()}:{End(p)}").Order(StringComparer.Ordinal));
        Assert.Equal(Ports(l2).Replace("Input:|OSD-SEL-V1.5|1 ", ""), Ports(l3).Replace("Input:|OSD-SEL-V1.5|1 ", ""));
        Assert.DoesNotContain("OSD-SEL-V1.5", Ports(l3));
    }

    [Fact]
    public void TheJsonInterfaceOfAnApplicationAndTheNeutralOneAreInstancesOfTheSameL2()
    {
        string Ports(JsonElement a) => string.Join(" ", a.GetProperty("ExternalPorts").EnumerateArray().Select(p => $"{p.GetProperty("Direction").GetString()}:{End(p)}").Order(StringComparer.Ordinal));
        var neutral = L3("1MMC-JSI-V2.5-I01"); var asm = L3("1MMC-JSI-V2.5-I02"); var l2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "MMC", "V2.5", "AIMs", "JSONInterface.json"))).RootElement;
        Assert.Equal(Ports(l2), Ports(neutral));
        Assert.Equal(Ports(l2), Ports(asm));

        // What makes it Audio Scene Management's is in the entries of its Access: the neutral L3 leaves them to be filled.
        static List<(string Name, string Uri)> Access(JsonElement a) => a.GetProperty("AccessSources").EnumerateArray().Select(e => (e.GetProperty("Name").GetString()!, e.GetProperty("Uri").GetString()!)).ToList();
        var neutralAccess = Access(neutral); var asmAccess = Access(asm);
        foreach (var name in new[] { "CommandSchema", "ResponseSchema", "ApplicationContext" })
        {
            Assert.StartsWith("/*", neutralAccess.Single(e => e.Name == name).Uri);
            Assert.StartsWith("https://", asmAccess.Single(e => e.Name == name).Uri);
        }
        Assert.EndsWith("CAE3/V1.0/data/UserCommand.json", asmAccess.Single(e => e.Name == "CommandSchema").Uri);
        Assert.EndsWith("Apps/CAE/ASM/Access/AsmApplicationContext.json", asmAccess.Single(e => e.Name == "ApplicationContext").Uri);
    }

    // The entries of the Access of the JSON Interface for Audio Scene Management are the files of the repository: the SHA-256 of each is that of its file.
    [Fact]
    public void TheAccessOfTheJsonInterfaceForAudioSceneManagementIsPinnedToItsFiles()
    {
        var entries = AIF.Controller.AccessEntry.ReadFrom(L3("1MMC-JSI-V2.5-I02"));
        string Hash(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal(Hash(JsiRig.UserCommandFile), entries.Single(e => e.Name == "CommandSchema").Sha256, ignoreCase: true);
        Assert.Equal(Hash(JsiRig.UserCommandFile), entries.Single(e => e.Name == "ResponseSchema").Sha256, ignoreCase: true);
        Assert.Equal(Hash(JsiRig.ContextFile), entries.Single(e => e.Name == "ApplicationContext").Sha256, ignoreCase: true);
        Assert.Empty(AIF.Controller.AccessEntry.Check(entries));
        Assert.Equal(["ApplicationContext", "CommandSchema", "ResponseSchema"], entries.Where(e => e.Setting.Length > 0).Select(e => e.Setting).Order().ToArray());
    }

    // ---- run ------------------------------------------------------------------------------------------------------------------------------------------

    private const string MoveViolin =
        "{\"Working\":\"MovedObjects violin: x=-1.5-1=-2.5.\",\"Command\":{\"UserCommandRQ\":{\"MovedObjects\":{\"Objects\":[{\"ObjectID\":\"violin\",\"NewSpatialAttitude\":{\"Position\":{\"CartPosition\":[-2.5,2,0]},\"Orientation\":{\"Orientation\":[0,0,180]}}}]}}}}";

    [Fact]
    public void WhatIsSaidBecomesACommandAndTheResponseIsSaid()
    {
        using var rig = new VciRig((_, _, _) => Task.FromResult(MoveViolin), prompt =>
            prompt.Contains("move the violin 1 metre to the left") ? "I moved the violin one metre to the left." : "By how many metres?");
        Assert.Equal(AifError.OK, rig.Start());

        // The application gives the Interface its Application Context (what it can be told to do and how), and then what the scene is, as the answer to a listing.
        Assert.Equal(AifError.OK, rig.Write("MMC-APC-V2.5", 1, File.ReadAllText(JsiRig.ContextFile)));
        Assert.Equal(AifError.OK, rig.Write(VciRig.Ucm, 1, JsiRig.Context(JsiRig.Scene()).ToJsonString()));

        // A person speaks. The words leave as the Heard Text; the command leaves at the Application Command Port.
        rig.Say("move the violin 1 metre to the left");
        var heard = rig.Read(VciRig.Bto, 1);
        Assert.True(heard.Ok, heard.Error.ToString());
        Assert.Equal("move the violin 1 metre to the left", MpaiJson.FromJson<BasicTextObject>(heard.Json!)!.GetText());
        var command = rig.Read(VciRig.Ucm, 1);
        Assert.True(command.Ok, command.Error.ToString());
        var aji = JsonNode.Parse(command.Json!)!;
        Assert.NotNull(aji["UserCommandRQ"]!["MovedObjects"]);
        Assert.Empty(new Mpai.Mmc.Jsi.SchemaCheck(JsiRig.UserCommandUri, JsiRig.UserCommandFile, Repository.Schemas).Violations(aji.ToJsonString())!);

        // A command that was sent is not said: the application may take time.
        Assert.False(rig.Read(VciRig.Bto, 2, 700).Ok);
        Assert.Empty(rig.Spoken);

        // The application answers, naming the command; the answer is said.
        var response = JsiRig.ResponseTo((string)aji["UserCommandID"]!, "Done", ("MovedObjects", "violin", "Done", null));
        Assert.Equal(AifError.OK, rig.Write(VciRig.Ucm, 1, response.ToJsonString()));
        var reply = rig.Read(VciRig.Bto, 2);
        Assert.True(reply.Ok, reply.Error.ToString());
        Assert.Equal("I moved the violin one metre to the left.", MpaiJson.FromJson<BasicTextObject>(reply.Json!)!.GetText());
        var speech = rig.Read(VciRig.Bso, 1);
        Assert.True(speech.Ok, speech.Error.ToString());
        Assert.Equal(["I moved the violin one metre to the left."], rig.Spoken);

        // The dialogue was told what the person said and the facts - not the command, not its JSON.
        var told = rig.Ollama!.Prompts.Single();
        Assert.Contains("What the person said: \"move the violin 1 metre to the left\"", told);
        Assert.Contains("Response Hint: The person had asked: \"move the violin 1 metre to the left\"", told);
        Assert.Contains("did moved, on violin", told);
        Assert.DoesNotContain("UserCommand", told);
        Assert.DoesNotContain("{", told);
    }

    [Fact]
    public void ARequestThatGivesNoCommandIsAnsweredAtOnceWithWhatIsMissing()
    {
        using var rig = new VciRig((_, _, _) => Task.FromResult(MoveViolin), _ => "By how many metres should I move the violin?");
        Assert.Equal(AifError.OK, rig.Start());
        Assert.Equal(AifError.OK, rig.Write(VciRig.Ucm, 1, JsiRig.Context(JsiRig.Scene()).ToJsonString()));

        rig.Say("move the violin a bit to the left");
        var reply = rig.Read(VciRig.Bto, 2);
        Assert.True(reply.Ok, reply.Error.ToString());
        Assert.Equal("By how many metres should I move the violin?", MpaiJson.FromJson<BasicTextObject>(reply.Json!)!.GetText());
        Assert.True(rig.Read(VciRig.Bso, 1).Ok);
        Assert.Equal(["By how many metres should I move the violin?"], rig.Spoken);
        Assert.False(rig.Read(VciRig.Ucm, 1, 500).Ok);                                  // no command was sent
        Assert.Empty(rig.JsiTurns);                                                     // and the language model of the Interface was not asked
        Assert.Contains("Response Hint: No command was formed. By how much", rig.Ollama!.Prompts.Single());
        Assert.Contains("What the person said: \"move the violin a bit to the left\"", rig.Ollama!.Prompts.Single());
    }

    // The Heard Text leaves the Interface whatever happens to the words.
    [Fact]
    public void WhatIsHeardLeavesAtTheHeardTextPortWhetherOrNotItBecomesACommand()
    {
        using var rig = new VciRig((_, _, _) => Task.FromResult(MoveViolin), _ => "ok");
        rig.Start();
        rig.Say("what is the weather like");
        var heard = rig.Read(VciRig.Bto, 1);
        Assert.True(heard.Ok, heard.Error.ToString());
        Assert.Equal("what is the weather like", MpaiJson.FromJson<BasicTextObject>(heard.Json!)!.GetText());
        Assert.True(rig.Read(VciRig.Bto, 2).Ok);                                         // it is answered: it is not for the application
        Assert.False(rig.Read(VciRig.Ucm, 1, 500).Ok);
    }
}
