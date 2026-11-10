using System.Text.Json.Nodes;

using Mpai.Xrv;

namespace Mpai.Aif.Tests;

// XRV-LTP V1.0, STAGE S1 - COMMON: typed access to the Data Types of the Cue engine, each validated against its schema (D:\AI\schemas\XRV1\V1.0 and the schemas it refers to), and the
// mini show (Test/Data/XRV/MiniShow): a Script with three Cues (Real, Virtual, Mixed), an Action Table, the Venue Specifications (two DMX fixtures, one virtual lamp), the
// Script-Venue Associations, a Participant Data and a Show Descriptors.
[Trait("Group", "Fast")]
public class XrvCommonTests
{
    private static readonly string[] Files =
        ["Script.json", "ActionTable.json", "REVenueSpecification.json", "VEVenueSpecification.json", "ScriptVenueAssociations.json", "ParticipantData.json", "ShowDescriptors.json"];

    // Each file is read by the class of its Data Type, which refuses what the schema does not allow, and gives back the same JSON.
    private static XrvData ReadFile(string file) => file switch
    {
        "Script.json" => Script.Read(XrvShow.Text(file)),
        "ActionTable.json" => ActionTable.Read(XrvShow.Text(file)),
        "REVenueSpecification.json" => REVenueSpecification.Read(XrvShow.Text(file)),
        "VEVenueSpecification.json" => VEVenueSpecification.Read(XrvShow.Text(file)),
        "ScriptVenueAssociations.json" => ScriptVenueAssociations.Read(XrvShow.Text(file)),
        "ParticipantData.json" => ParticipantData.Read(XrvShow.Text(file)),
        "ShowDescriptors.json" => ShowDescriptors.Read(XrvShow.Text(file)),
        _ => throw new ArgumentException(file)
    };

    [Fact]
    public void EveryFileOfTheMiniShowValidatesAgainstItsSchema()
    {
        foreach (var file in Files)
            Assert.Empty(ReadFile(file).Violations());
        Assert.Equal(Files.Length, Directory.GetFiles(XrvShow.Folder, "*.json").Length);        // no file of the mini show is left unchecked
    }

    [Fact]
    public void EveryFileReadsBackUnchanged()
    {
        foreach (var file in Files)
        {
            var typed = ReadFile(file);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(XrvShow.Text(file)), typed.Json), file + " changed when read");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(XrvShow.Text(file)), JsonNode.Parse(typed.ToJson())), file + " changed when written");
        }
    }

    [Fact]
    public void TheScriptHasThreeCuesOfTheThreeKinds()
    {
        var script = Script.Read(XrvShow.Text("Script.json"));
        Assert.Equal(new[] { ("C1", CueKind.Real), ("C2", CueKind.Virtual), ("C3", CueKind.Mixed) }, script.Cues.Select(c => (c.Id, c.Kind)).ToArray());
        Assert.False(script.HasOperator);
        Assert.Single(script.Cues[0].Groups);
        Assert.Equal(2, script.Cues[1].Groups.Count);                                  // two groups of Conditions, two ways on
        Assert.Equal(new[] { "C3", "C3" }, script.Cues[1].Groups.Select(g => g.NextCue).ToArray());
        Assert.Null(script.Cues[2].Groups[0].NextCue);                                // the last Cue: the show ends
        Assert.Equal(new[] { StatusKind.REParticipant, StatusKind.VEPerformance }, script.Cues[2].Groups[0].Targets.Select(t => t.Kind).ToArray());   // a Mixed Cue: Conditions of both sides
        Assert.NotNull(script.Find("C2"));
        Assert.Null(script.Find("C9"));
    }

    [Fact]
    public void EveryActionTheCuesNameIsInTheActionTableAndHasAnAssociation()
    {
        var script = Script.Read(XrvShow.Text("Script.json"));
        var table = ActionTable.Read(XrvShow.Text("ActionTable.json"));
        var associations = ScriptVenueAssociations.Read(XrvShow.Text("ScriptVenueAssociations.json"));
        var named = script.Cues.SelectMany(c => c.Groups).SelectMany(g => g.REActions.Concat(g.VEActions)).Distinct().Order().ToArray();
        Assert.Equal(table.ActionIds.Order().ToArray(), named);
        foreach (var id in named) Assert.NotNull(table.Commands(id));
        var associated = associations.ActionElements(true).Concat(associations.ActionElements(false)).Select(a => a.ActionId).Order().ToArray();
        Assert.Equal(named, associated);
    }

    [Fact]
    public void TheVenueSpecificationsHaveTwoDmxFixturesAndOneVirtualLamp()
    {
        var real = REVenueSpecification.Read(XrvShow.Text("REVenueSpecification.json"));
        Assert.Equal(new[] { (1, 1, "Dimmer", "FX1"), (1, 2, "Dimmer", "FX2") }, real.DmxChannels.Select(c => (c.Universe, c.Channel, c.Definition, c.FixtureId)).ToArray());
        var virt = VEVenueSpecification.Read(XrvShow.Text("VEVenueSpecification.json"));
        Assert.Equal(new[] { "VLamp1" }, virt.VirtualObjectIds.ToArray());
    }

    [Fact]
    public void ADatumThatTheSchemaDoesNotAllowIsNotRead()
    {
        var noId = XrvShow.ScriptWith(s => s.Remove("ScriptID"));
        Assert.Contains("ScriptID", Assert.Throws<FormatException>(() => Script.Read(noId)).Message);
        var wrongHeader = XrvShow.ScriptWith(s => s["Header"] = "XRV-ATB-V1.0");
        Assert.Throws<FormatException>(() => Script.Read(wrongHeader));
        var extra = XrvShow.ScriptWith(s => s["Invented"] = 1);
        Assert.Throws<FormatException>(() => Script.Read(extra));
        var badGroup = XrvShow.ScriptWith(s => s["CueList"]![0]!["Cue"]![0]!["RealCue"]!.AsObject().Remove("RECueID"));
        Assert.Throws<FormatException>(() => Script.Read(badGroup));
        Assert.Throws<FormatException>(() => ActionTable.Read("""{"Header":"XRV-ATB-V1.0"}"""));
        Assert.Throws<FormatException>(() => ActionTable.Read("[1]"));
    }

    [Fact]
    public void TheDescriptorsAndTheStatusesOfAnyOfTheSixKindsValidate()
    {
        foreach (var re in new[] { true, false })
        {
            var s = re ? "RE" : "VE";
            Assert.Empty(DescriptorsObject.Read(re ? StatusKind.REPerformance : StatusKind.VEPerformance, XrvShow.PerformanceDescriptors(re, "d1", "Anna")).Violations());
            Assert.Empty(DescriptorsObject.Read(re ? StatusKind.REParticipant : StatusKind.VEParticipant, XrvShow.ParticipantDescriptors(re, "d2", ("P1", "S1"))).Violations());
            Assert.Empty(DescriptorsObject.Read(re ? StatusKind.REOperator : StatusKind.VEOperator, XrvShow.OperatorDescriptors(re, "d3", "Go")).Violations());

            Assert.Equal(re ? "XRV-RFS-V1.0" : "XRV-VFS-V1.0", StatusObject.Read(XrvShow.Status(re ? StatusKind.REPerformance : StatusKind.VEPerformance, "s1", XrvShow.Performers(re, "Anna"))).Header);
            Assert.Equal(re ? StatusKind.REParticipant : StatusKind.VEParticipant, StatusObject.Read(XrvShow.Status(re ? StatusKind.REParticipant : StatusKind.VEParticipant, "s2", XrvShow.Participants(re, "P1"))).Kind);
            Assert.Equal("Hold", StatusObject.Read(XrvShow.ShowControlStatus(re, "s3", "Hold")).ShowControl);
            Assert.Contains(s, KindInfo.OfStatusHeader(re ? "XRV-ROS-V1.0" : "XRV-VOS-V1.0").Side);
        }
        Assert.Throws<FormatException>(() => StatusObject.Read(XrvShow.ShowControlStatus(true, "s4", "Stop")));              // not a ShowControl of the schema
    }

    [Fact]
    public void TheIdsAreBasicTextObjects()
    {
        var id = TextId.Make("cue-1", "C2", "UE1");
        Assert.Empty(id.Violations());
        Assert.Equal("C2", TextId.Read(id.ToJson()).Text);
        Assert.Throws<FormatException>(() => TextId.Read("""{"Header":"OSD-BTO-V1.5"}"""));                                     // no BasicTextObjectID
    }

    [Fact]
    public void TheCueListAsItsOwnDataTypeIsTheListOfTheScript()
    {
        var script = Script.Read(XrvShow.Text("Script.json"));
        var list = new JsonObject
        {
            ["Header"] = "XRV-CLS-V1.0", ["CueListID"] = "cl", ["UEnvironmentID"] = "UE1", ["CueListTime"] = XrvTime.Simple("cl-t", 0),
            ["CueListData"] = script.Json["CueList"]!.DeepClone()
        };
        var typed = CueList.Read(list.ToJsonString());
        Assert.Equal(script.Cues.Select(c => c.Id), typed.Cues.Select(c => c.Id));
    }

    [Fact]
    public void AStatusMeetsATargetWhenTheElementsTheTargetNamesAreEqual()
    {
        JsonNode N(string json) => JsonNode.Parse(json)!;
        // names the elements, extra elements in the Status are fine
        Assert.True(JsonMatch.Meets(N("""{"IndividualStatuses":[{"REPerformerID":"Anna"}]}"""), N("""{"IndividualStatuses":[{"REPerformerID":"Bob"},{"REPerformerID":"Anna","SeatID":"x"}]}""")));
        Assert.False(JsonMatch.Meets(N("""{"IndividualStatuses":[{"REPerformerID":"Anna"}]}"""), N("""{"IndividualStatuses":[{"REPerformerID":"Bob"}]}""")));
        Assert.False(JsonMatch.Meets(N("""{"IndividualStatuses":[{"REPerformerID":"Anna"}]}"""), N("""{}""")));
        // numbers by value, the identity of a nested Object is not compared, a plain ID is
        Assert.True(JsonMatch.Meets(N("""{"P":{"Header":"OSD-OPS-V1.5","PositionID":"a","CartPosition":[1,2.0,3]}}"""), N("""{"P":{"Header":"OSD-OPS-V1.5","PositionID":"b","CartPosition":[1.0,2,3]}}""")));
        Assert.False(JsonMatch.Meets(N("""{"REPerformerID":"A"}"""), N("""{"REPerformerID":"B"}""")));
        Assert.True(JsonMatch.Meets(N("""{}"""), N("""{"x":1}""")));
    }

    [Fact]
    public void ADerivedStatusHasTheElementsOfTheDescriptorsAndValidates()
    {
        var descriptors = DescriptorsObject.Read(StatusKind.REParticipant, XrvShow.ParticipantDescriptors(true, "d", ("P1", "S1"), ("P2", null)));
        var status = Interpretation.Derive(descriptors, "s-1", 1000);
        Assert.Empty(status.Violations());
        Assert.Equal("S1", status.Data!["IndividualStatuses"]![0]!["SeatID"]!.GetValue<string>());
        var operatorDescriptors = DescriptorsObject.Read(StatusKind.VEOperator, XrvShow.OperatorDescriptors(false, "o", "Skip"));
        var operatorStatus = Interpretation.Derive(operatorDescriptors, "s-2", 1000);
        Assert.Empty(operatorStatus.Violations());
        Assert.Equal("Skip", operatorStatus.ShowControl);
        Assert.Null(Interpretation.Derive(DescriptorsObject.Read(StatusKind.REOperator, XrvShow.OperatorDescriptors(true, "o2")), "s-3", 1).ShowControl);
    }
}
