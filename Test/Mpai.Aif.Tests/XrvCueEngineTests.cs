using System.Text.Json;
using System.Text.Json.Nodes;

using AIF.Controller;
using AIF.Metadata;
using AIF.Store;
using Mpai.Xrv;
using Mpai.Xrv.Acg;

namespace Mpai.Aif.Tests;

// XRV-LTP V1.0, STAGE S2 - THE CUE ENGINE: Action Generation (XRV-ACG) and the three Status Interpretation AIMs (XRV-PFI, XRV-PTI, XRV-OSI).
//
// Two levels. The Cue engine of Action Generation is a state machine and is tested alone, fast and in every detail (the events it asks the AIM to write: the Cue Point ID, the RE and
// the VE Action IDs). Then the four AIMs run as a TEST Module under the Controller (ControllerApi + the XRV provider), driven by recorded Descriptors that the test writes and replays
// in order; what they write on the Ports of the Module is read back. No device and no Environment Description: the recordings are the Descriptors.
[Trait("Group", "Fast")]
public class XrvCueEngineTests
{
    // ---- the engine ----------------------------------------------------------------------------------------------------------------------------------

    private static readonly JsonNode Anna = XrvShow.Performers(true, "Anna");
    private static string SAnna => XrvShow.Status(StatusKind.REPerformance, "s-anna", Anna);
    private static string SBob => XrvShow.Status(StatusKind.REPerformance, "s-bob", XrvShow.Performers(true, "Bob"));
    private static string SGuest1 => XrvShow.Status(StatusKind.VEParticipant, "s-g1", XrvShow.Participants(false, "Guest1"));
    private static string SGuest2 => XrvShow.Status(StatusKind.VEParticipant, "s-g2", XrvShow.Participants(false, "Guest2"));
    private static string SSeat => XrvShow.Status(StatusKind.REParticipant, "s-seat", new JsonObject { ["IndividualStatuses"] = new JsonArray(new JsonObject { ["REParticipantID"] = "P1", ["SeatID"] = "S1" }) });
    private static string SBot => XrvShow.Status(StatusKind.VEPerformance, "s-bot", XrvShow.Performers(false, "Bot"));

    private static readonly ActionTable Table = ActionTable.Read(XrvShow.Text("ActionTable.json"));

    private static CueEngine Engine(string? script = null, List<string>? reports = null) =>
        new(Script.Read(script ?? XrvShow.Text("Script.json")), Table, reports is null ? null : reports.Add);

    private static string[] Shown(IEnumerable<EngineEvent> events) =>
        events.Select(e => (e.Kind switch { EventKind.CuePoint => "cue:", EventKind.REAction => "re:", _ => "ve:" }) + e.Id).ToArray();

    private static string[] Feed(CueEngine engine, string status) => Shown(engine.Receive(StatusObject.Read(status)));

    private static string WithOperator(string script) => XrvShow.ScriptWith(s => s["Operators"] = new JsonArray(new JsonObject { ["Consoles"] = new JsonArray(new JsonObject { ["Name"] = "Show console", ["Mnemonic"] = "SHC" }) }));

    // 1. THE SEQUENCE OF ACTION IDs of the recorded mini show.
    [Fact]
    public void TheMiniShowGivesTheExpectedSequenceOfActionIds()
    {
        var engine = Engine();
        var seen = new List<string>();
        seen.AddRange(Shown(engine.Start()));
        Assert.Equal(new[] { "cue:C1" }, seen);
        foreach (var status in new[] { SAnna, SGuest1, SSeat, SBot })
            seen.AddRange(Feed(engine, status));
        Assert.Equal(new[] { "cue:C1", "re:RE-LIGHTS-UP", "cue:C2", "ve:VE-LAMP-ON", "cue:C3", "re:RE-LIGHTS-OFF", "ve:VE-LAMP-OFF" }, seen);
        Assert.Equal(ShowState.Ended, engine.State);
        Assert.Empty(Feed(engine, SAnna));                                         // nothing after the last Cue
    }

    // 2. NO CONDITION MET: Action Generation waits, and sends no new Cue Point ID.
    [Fact]
    public void WhenNoConditionIsMetTheEngineWaits()
    {
        var engine = Engine();
        Assert.Equal(new[] { "cue:C1" }, Shown(engine.Start()));
        Assert.Empty(Feed(engine, SBob));                                          // the right kind of Status, not the target
        Assert.Empty(Feed(engine, SGuest1));                                       // a Status that a later Cue wants
        Assert.Empty(Feed(engine, SSeat));
        Assert.Equal("C1", engine.CurrentCue);
        Assert.Equal(ShowState.Running, engine.State);
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Feed(engine, SAnna));  // the target, at last
    }

    // 3. SEVERAL GROUPS OF CONDITIONS branch to the right next Cue; the first group met, in the order of the Cue List, wins.
    [Fact]
    public void ACueWithSeveralGroupsBranchesToTheRightNextCue()
    {
        var script = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(
            XrvShow.RealCue("C1",
                XrvShow.Group([(StatusKind.REPerformance, XrvShow.Performers(true, "Anna"))], ["RE-LIGHTS-UP"], [], "C3"),
                XrvShow.Group([(StatusKind.REPerformance, XrvShow.Performers(true, "Bob"))], ["RE-LIGHTS-OFF"], [], "C2")),
            XrvShow.VirtualCue("C2", XrvShow.Group([], [], ["VE-LAMP-ON"])),                  // no Conditions: met at once; no NextCue: the Cue after it in the list
            XrvShow.VirtualCue("C3", XrvShow.Group([], [], ["VE-LAMP-OFF"], "C9"))));        // C9 is not a Cue

        var viaBob = Engine(script);
        viaBob.Start();
        Assert.Equal(new[] { "re:RE-LIGHTS-OFF", "cue:C2", "ve:VE-LAMP-ON", "cue:C3", "ve:VE-LAMP-OFF" }, Feed(viaBob, SBob));
        Assert.Equal(ShowState.Ended, viaBob.State);                                       // C9 is no Cue: the show ends, and says so

        var reports = new List<string>();
        var viaAnna = Engine(script, reports);
        viaAnna.Start();
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C3", "ve:VE-LAMP-OFF" }, Feed(viaAnna, SAnna));    // Anna: C1 goes to C3, not C2
        Assert.Contains(reports, r => r.Contains("C9"));

        // Two groups met by the same Status: the first in the list.
        var twice = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(
            XrvShow.RealCue("C1", XrvShow.Group([(StatusKind.REPerformance, Anna)], ["RE-LIGHTS-UP"], [], "C2"), XrvShow.Group([(StatusKind.REPerformance, Anna)], ["RE-LIGHTS-OFF"], [], "C3")),
            XrvShow.VirtualCue("C2", XrvShow.Group([(StatusKind.VEPerformance, XrvShow.Performers(false, "Bot"))], [], ["VE-LAMP-ON"])),
            XrvShow.VirtualCue("C3", XrvShow.Group([(StatusKind.VEPerformance, XrvShow.Performers(false, "Bot"))], [], ["VE-LAMP-OFF"]))));
        var first = Engine(twice); first.Start();
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Feed(first, SAnna));
    }

    // The mini show itself branches: the two groups of C2 give different Actions and the same next Cue.
    [Fact]
    public void TheVirtualCueOfTheMiniShowHasTwoWaysOn()
    {
        var engine = Engine(); engine.Start(); Feed(engine, SAnna);
        Assert.Equal(new[] { "ve:VE-LAMP-DIM", "cue:C3" }, Feed(engine, SGuest2));
    }

    // 4. THE MIXED CUE: Conditions of both sides, Actions of both sides; the Action IDs name the Action Table entries as any other.
    [Fact]
    public void TheMixedCueNeedsTheConditionsOfBothSides()
    {
        foreach (var order in new[] { new[] { SSeat, SBot }, new[] { SBot, SSeat } })
        {
            var engine = Engine(XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(s["CueList"]![2]!.DeepClone())));      // the Mixed Cue alone
            Assert.Equal(new[] { "cue:C3" }, Shown(engine.Start()));
            Assert.Empty(Feed(engine, order[0]));                                                                          // one side is not enough
            var done = Feed(engine, order[1]);
            Assert.Equal(new[] { "re:RE-LIGHTS-OFF", "ve:VE-LAMP-OFF" }, done);
            Assert.True(done.Select(d => d[3..]).All(Table.Has));
            Assert.Equal(ShowState.Ended, engine.State);
        }
        // An Action ID that is not in the Action Table is not executed.
        var reports = new List<string>();
        var bad = Engine(XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(XrvShow.MixedCue("M1", XrvShow.Group([], ["RE-LIGHTS-UP", "NOT-IN-TABLE"], ["VE-LAMP-ON"])))), reports);
        Assert.Equal(new[] { "cue:M1", "re:RE-LIGHTS-UP", "ve:VE-LAMP-ON" }, Shown(bad.Start()));
        Assert.Contains(reports, r => r.Contains("NOT-IN-TABLE"));
    }

    // 5. SHOW CONTROL: Go, Hold, Skip, Back; the four states; a show with no operator.
    [Fact]
    public void GoStartsTheShowAndHoldHoldsIt()
    {
        var engine = Engine(WithOperator(""));
        Assert.Empty(engine.Start());
        Assert.Equal(ShowState.NotStarted, engine.State);
        Assert.Empty(Feed(engine, SAnna));                                                      // nothing runs before Go
        Assert.Empty(Feed(engine, XrvShow.ShowControlStatus(false, "c0", "Hold")));            // nothing to hold yet
        Assert.Equal(ShowState.NotStarted, engine.State);
        Assert.Equal(new[] { "cue:C1" }, Feed(engine, XrvShow.ShowControlStatus(true, "c1", "Go")));
        Assert.Equal(ShowState.Running, engine.State);
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Feed(engine, SAnna));
        Assert.Empty(Feed(engine, XrvShow.ShowControlStatus(false, "c2", "Hold")));
        Assert.Equal(ShowState.Held, engine.State);
        Assert.Empty(Feed(engine, SGuest1));                                                    // held: no Action, no Cue Point ID
    }

    [Fact]
    public void AHeldShowGoesOnWhereItWasOnGo()
    {
        var engine = Engine(WithOperator(""));
        engine.Start(); engine.Control("Go"); Feed(engine, SAnna);
        engine.Control("Hold");
        Assert.Empty(Feed(engine, SGuest1));                                                    // received during the Hold, kept
        Assert.Equal(ShowState.Held, engine.State);
        Assert.Equal(new[] { "ve:VE-LAMP-ON", "cue:C3" }, Shown(engine.Control("Go")));
        Assert.Equal(ShowState.Running, engine.State);
    }

    // THE OPERATOR MOVES ON (the author, 2026-11-10): Go in a running show fires the waiting Cue - its Actions, then the next Cue.
    [Fact]
    public void GoInARunningShowFiresTheWaitingCue()
    {
        var engine = Engine(WithOperator(""));
        engine.Start(); engine.Control("Go");
        Assert.Empty(Feed(engine, SBob));                                                       // C1 waits for Anna: Bob does not meet it
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Shown(engine.Control("Go")));       // the operator moves on: C1's Actions, then C2
        Assert.Equal("C2", engine.CurrentCue);
        Assert.Equal(ShowState.Running, engine.State);
    }

    [Fact]
    public void ACueWhoseConditionsAreNeverMetWaitsForAGoOrASkip()
    {
        var engine = Engine(WithOperator(""));
        engine.Start(); engine.Control("Go");
        Assert.Empty(Feed(engine, SBob));
        Assert.Equal("C1", engine.CurrentCue);
        Assert.Equal(new[] { "cue:C2" }, Shown(engine.Control("Skip")));                        // Skip leaves the Cue: no Action of it
        Assert.Equal(new[] { "cue:C3" }, Shown(engine.Control("Skip")));
        Assert.Empty(engine.Control("Skip"));                                                   // after the last Cue the show has ended
        Assert.Equal(ShowState.Ended, engine.State);
        Assert.Empty(engine.Control("Go")); Assert.Empty(engine.Control("Back")); Assert.Empty(engine.Control("Hold"));
        Assert.Equal(ShowState.Ended, engine.State);
    }

    [Fact]
    public void BackReturnsToTheCueBefore()
    {
        var engine = Engine(WithOperator(""));
        engine.Start(); engine.Control("Go");
        Assert.Empty(engine.Control("Back"));                                                   // at the first Cue there is none before
        Assert.Equal("C1", engine.CurrentCue);
        engine.Control("Skip");
        Assert.Equal(new[] { "cue:C1" }, Shown(engine.Control("Back")));
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Feed(engine, SAnna));              // and the Cue runs again
        Assert.Equal(new[] { "cue:C1" }, Shown(engine.Control("Back")));                        // back goes by the Cues the show went through
    }

    [Fact]
    public void ASkipOrABackOfAHeldShowTellsTheCuePointOnGo()
    {
        var engine = Engine(WithOperator(""));
        engine.Start(); engine.Control("Go"); engine.Control("Hold");
        Assert.Empty(engine.Control("Skip"));                                                   // held: no new Cue Point ID
        Assert.Equal("C2", engine.CurrentCue);
        Assert.Equal(new[] { "cue:C2" }, Shown(engine.Control("Go")));
    }

    [Fact]
    public void AShowWithNoOperatorRunsFromTheFirstToTheLastCue()
    {
        var engine = Engine();
        Assert.Equal(new[] { "cue:C1" }, Shown(engine.Start()));
        Assert.Equal(ShowState.Running, engine.State);
        Assert.Empty(engine.Control("Go"));
        var unconditional = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(
            XrvShow.RealCue("C1", XrvShow.Group([], ["RE-LIGHTS-UP"], [])), XrvShow.VirtualCue("C2", XrvShow.Group([], [], ["VE-LAMP-ON"])), XrvShow.MixedCue("C3", XrvShow.Group([], ["RE-LIGHTS-OFF"], ["VE-LAMP-OFF"]))));
        Assert.Equal(new[] { "cue:C1", "re:RE-LIGHTS-UP", "cue:C2", "ve:VE-LAMP-ON", "cue:C3", "re:RE-LIGHTS-OFF", "ve:VE-LAMP-OFF" }, Shown(Engine(unconditional).Start()));
    }

    // 6. A NEW CUE POINT ID RESETS THE TASK of the one before: what had been seen for the earlier Cue is forgotten.
    [Fact]
    public void ANewCueForgetsTheStatusesOfTheCueBefore()
    {
        var script = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(
            XrvShow.RealCue("C1", XrvShow.Group([(StatusKind.REPerformance, Anna)], ["RE-LIGHTS-UP"], [], "C2")),
            XrvShow.RealCue("C2", XrvShow.Group([(StatusKind.REPerformance, Anna)], ["RE-LIGHTS-OFF"], []))));
        var engine = Engine(script); engine.Start();
        Assert.Equal(new[] { "re:RE-LIGHTS-UP", "cue:C2" }, Feed(engine, SAnna));
        Assert.Equal("C2", engine.CurrentCue);                                                 // the Anna of C1 is not the Anna of C2
        Assert.Equal(new[] { "re:RE-LIGHTS-OFF" }, Feed(engine, SAnna));
    }

    [Fact]
    public void ConditionsOnDescriptorsAreNotEvaluated()
    {
        var reports = new List<string>();
        var script = XrvShow.ScriptWith(s => s["CueList"]![0]!["Cue"]![0]!["RealCue"]!["REGroups"]![0]!["RECueConditions"]!["REPerformanceDescriptors"] = JsonNode.Parse(XrvShow.PerformanceDescriptors(true, "d", "Anna")));
        var engine = Engine(script, reports);
        engine.Start();
        Assert.Empty(Feed(engine, SAnna));
        Assert.Contains(reports, r => r.Contains("Descriptors"));
    }

    // ---- the four AIMs, as a Module under the Controller ----------------------------------------------------------------------------------------------

    // 1. The recorded mini show, replayed in order: each frame follows the Cue Point ID that asks for it.
    [Fact]
    public void TheModuleGivesTheExpectedActionIdsForTheRecordedMiniShow()
    {
        using var rig = new XrvModuleRig();
        rig.Give(XrvShow.Text("Script.json"));
        Assert.Equal("C1", rig.ReadId(3));
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Anna"));
        Assert.Equal("RE-LIGHTS-UP", rig.ReadId(1));
        Assert.Equal("C2", rig.ReadId(3));
        rig.Write("XRV-VTD-V1.0", XrvShow.ParticipantDescriptors(false, "vtd-1", ("Guest1", null)));
        Assert.Equal("VE-LAMP-ON", rig.ReadId(2));
        Assert.Equal("C3", rig.ReadId(3));
        rig.Write("XRV-RTD-V1.0", XrvShow.ParticipantDescriptors(true, "rtd-1", ("P1", "S1")));
        Assert.Null(rig.ReadId(1, 700));                                                       // the Mixed Cue needs both sides
        rig.Write("XRV-VFD-V1.0", XrvShow.PerformanceDescriptors(false, "vfd-1", "Bot"));
        Assert.Equal("RE-LIGHTS-OFF", rig.ReadId(1));
        Assert.Equal("VE-LAMP-OFF", rig.ReadId(2));
        Assert.Null(rig.ReadId(3, 700)); Assert.Null(rig.ReadId(1, 100)); Assert.Null(rig.ReadId(2, 100));      // the show has ended: nothing more
    }

    // The same recording, all frames written at once: the Descriptors wait on their Ports for the Cue Point ID that asks for them.
    [Fact]
    public void TheWholeRecordingWrittenAtOnceGivesTheSameSequence()
    {
        using var rig = new XrvModuleRig();
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Anna"));
        rig.Write("XRV-VTD-V1.0", XrvShow.ParticipantDescriptors(false, "vtd-1", ("Guest1", null)));
        rig.Write("XRV-RTD-V1.0", XrvShow.ParticipantDescriptors(true, "rtd-1", ("P1", "S1")));
        rig.Write("XRV-VFD-V1.0", XrvShow.PerformanceDescriptors(false, "vfd-1", "Bot"));
        rig.Give(XrvShow.Text("Script.json"));
        Assert.Equal(new[] { "C1", "C2", "C3" }, rig.ReadIds(3, 3));
        Assert.Equal(new[] { "RE-LIGHTS-UP", "RE-LIGHTS-OFF" }, rig.ReadIds(1, 2));
        Assert.Equal(new[] { "VE-LAMP-ON", "VE-LAMP-OFF" }, rig.ReadIds(2, 2));
        Assert.Null(rig.ReadId(3, 700));
    }

    // 2. No Condition met: the Module waits, sends no new Cue Point ID, and is not degraded for it.
    [Fact]
    public void TheModuleWaitsWhenNoConditionIsMet()
    {
        using var rig = new XrvModuleRig();
        rig.Give(XrvShow.Text("Script.json"));
        Assert.Equal("C1", rig.ReadId(3));
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Bob"));
        rig.Write("XRV-VTD-V1.0", XrvShow.ParticipantDescriptors(false, "vtd-1", ("Guest1", null)));
        Assert.Null(rig.ReadId(3, 800)); Assert.Null(rig.ReadId(1, 100)); Assert.Null(rig.ReadId(2, 100));
        Assert.Equal(AifError.OK, rig.Status.Error);
        Assert.DoesNotContain(rig.Status.Aims, a => a.Status != AimStatus.Alive);
    }

    // 5. Show control through the Operator Status Interpretation: Go starts the show, Hold holds it, Go resumes it, Skip and Back move the Cue Point.
    [Fact]
    public void TheOperatorControlsTheShowThroughTheOperatorDescriptors()
    {
        using var rig = new XrvModuleRig();
        rig.Give(WithOperator(""));
        Assert.Null(rig.ReadId(3, 800));                                                       // not started: nothing is sent
        rig.Write("XRV-ROD-V1.0", XrvShow.OperatorDescriptors(true, "rod-1", "Go"));
        Assert.Equal("C1", rig.ReadId(3));
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Anna"));
        Assert.Equal("RE-LIGHTS-UP", rig.ReadId(1));
        Assert.Equal("C2", rig.ReadId(3));
        rig.Write("XRV-VOD-V1.0", XrvShow.OperatorDescriptors(false, "vod-1", "Hold"));
        Thread.Sleep(1000);                                                          // the Hold goes through a different AIM from the Participant Descriptors: let it arrive first
        rig.Write("XRV-VTD-V1.0", XrvShow.ParticipantDescriptors(false, "vtd-1", ("Guest1", null)));
        Assert.Null(rig.ReadId(2, 800));                                                       // held: no Action
        rig.Write("XRV-ROD-V1.0", XrvShow.OperatorDescriptors(true, "rod-2", "Go"));
        Assert.Equal("VE-LAMP-ON", rig.ReadId(2));
        Assert.Equal("C3", rig.ReadId(3));
        rig.Write("XRV-ROD-V1.0", XrvShow.OperatorDescriptors(true, "rod-3", "Back"));
        Assert.Equal("C2", rig.ReadId(3));
        rig.Write("XRV-ROD-V1.0", XrvShow.OperatorDescriptors(true, "rod-4", "Skip"));
        Assert.Equal("C3", rig.ReadId(3));
    }

    // 6. A new Cue Point ID resets the task of the one before, on the Port: the Cue Point ID input of an Interpretation AIM has Depth 1 and DropOldest, so of the Cue Point IDs that wait on
    // it the AIM reads the last. Three Cue Point IDs wait on the Port of the Performance Status Interpretation (a Module of that AIM alone, held at the start), and the Descriptors have
    // the performers of the first two Cues: if the first or the second Cue Point ID were read, a Status would come.
    [Fact]
    public void OfTheCuePointIdsThatWaitOnThePortTheAimReadsTheLast()
    {
        var script = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(
            XrvShow.RealCue("C1", XrvShow.Group([(StatusKind.REPerformance, XrvShow.Performers(true, "Anna"))], [], [])),
            XrvShow.RealCue("C2", XrvShow.Group([(StatusKind.REPerformance, XrvShow.Performers(true, "Bob"))], [], [])),
            XrvShow.RealCue("C3", XrvShow.Group([(StatusKind.REPerformance, XrvShow.Performers(true, "Dora"))], [], []))));
        var gate = new TaskCompletionSource();
        using var rig = new XrvModuleRig(gate.Task, only: XrvProvider.Pfi);
        rig.Write("XRV-SCR-V1.0", script);
        foreach (var cue in new[] { "C1", "C2", "C3" }) rig.Write("OSD-BTO-V1.5", TextId.Make("c-" + cue, cue, "UE1").ToJson());
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Anna", "Bob"));
        gate.SetResult();
        Assert.Null(rig.ReadRaw("XRV-RFS-V1.0", 1, 1500));                                      // only C3 was read: Dora is not there
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-2", "Anna", "Dora"));
        var status = StatusObject.Read(rig.ReadRaw("XRV-RFS-V1.0")!);                           // Dora is there, and the Status is the one of the Descriptors
        Assert.Contains("Dora", status.Data!.ToJsonString());

        // and a new Cue Point ID replaces the task: C1 now, Anna is among the performers of the last Descriptors: the Status comes at once.
        rig.Write("OSD-BTO-V1.5", TextId.Make("c-again", "C1", "UE1").ToJson());
        Assert.Contains("Anna", StatusObject.Read(rig.ReadRaw("XRV-RFS-V1.0")!).Data!.ToJsonString());
    }

    // 7. Participant Data: Action Generation passes it through unchanged; the other AIMs receive it and do nothing with it.
    [Fact]
    public void ParticipantDataPassesThroughActionGenerationUnchanged()
    {
        using var rig = new XrvModuleRig();
        var data = XrvShow.Text("ParticipantData.json");
        rig.Give(XrvShow.Text("Script.json"));
        Assert.Equal(AifError.OK, rig.Write("XRV-PTD-V1.0", data));
        var out_ = rig.ReadRaw("XRV-PTD-V1.0");
        Assert.NotNull(out_);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data), JsonNode.Parse(out_!)));
        Assert.Null(rig.ReadRaw("XRV-PTD-V1.0", 1, 500));                                        // once: the other three AIMs did not send it on
        // and the show goes on as before
        Assert.Equal("C1", rig.ReadId(3));
        rig.Write("XRV-RFD-V1.0", XrvShow.PerformanceDescriptors(true, "rfd-1", "Anna"));
        Assert.Equal("RE-LIGHTS-UP", rig.ReadId(1));
    }

    [Theory]
    [InlineData("1XRV-PFI-V1.0-I01")]
    [InlineData("1XRV-PTI-V1.0-I01")]
    [InlineData("1XRV-OSI-V1.0-I01")]
    public void AnInterpretationAimIgnoresTheParticipantData(string aim)
    {
        using var rig = new XrvModuleRig(only: aim);
        rig.Write("XRV-PTD-V1.0", XrvShow.Text("ParticipantData.json"));
        Assert.Equal(AifError.OK, rig.Status.Error);
        Assert.DoesNotContain(rig.Status.Aims, a => a.Status != AimStatus.Alive);
        var (family, re, kind) = aim.Contains("PFI") ? ("Performance", "XRV-RFD-V1.0", StatusKind.REPerformance) : aim.Contains("PTI") ? ("Participant", "XRV-RTD-V1.0", StatusKind.REParticipant) : ("Operator", "XRV-ROD-V1.0", StatusKind.REOperator);
        Assert.NotNull(family);
        // it still works: the Script, a Cue Point ID and the Descriptors give a Status
        var script = XrvShow.ScriptWith(s => s["CueList"] = new JsonArray(family switch
        {
            "Performance" => XrvShow.RealCue("C1", XrvShow.Group([(kind, XrvShow.Performers(true, "Anna"))], [], [])),
            "Participant" => XrvShow.RealCue("C1", XrvShow.Group([(kind, XrvShow.Participants(true, "P1"))], [], [])),
            _ => XrvShow.RealCue("C1", XrvShow.Group([], [], []))
        }));
        rig.Write("XRV-SCR-V1.0", script);
        rig.Write("OSD-BTO-V1.5", TextId.Make("c", "C1", "UE1").ToJson());
        var descriptors = family switch
        {
            "Performance" => XrvShow.PerformanceDescriptors(true, "d", "Anna"),
            "Participant" => XrvShow.ParticipantDescriptors(true, "d", ("P1", null)),
            _ => XrvShow.OperatorDescriptors(true, "d", "Go")
        };
        rig.Write(re, descriptors);
        Assert.NotNull(rig.ReadRaw(StatusObjectType(kind)));
    }

    private static string StatusObjectType(StatusKind kind) => KindInfo.Of(kind).StatusHeader;

    // 8. THE L3 OF EACH AIM is an instance of its L2, loads in the Controller, and its Ports are the L2's.
    private static readonly string[] Aims = ["1XRV-ACG-V1.0-I01", "1XRV-PFI-V1.0-I01", "1XRV-PTI-V1.0-I01", "1XRV-OSI-V1.0-I01"];

    [Fact]
    public void TheL3OfEachAimIsAnInstanceOfItsL2()
    {
        JsonElement? FindL3(string instance)
        {
            var file = Path.Combine(Repository.Amds, instance + ".json");
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone() : null;
        }
        var conformance = new L2Conformance(Repository.Schemas, FindL3);
        foreach (var aim in Aims)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")));
            Assert.Empty(conformance.Check(doc.RootElement));
        }
    }

    [Fact]
    public void TheL3sLoadInTheControllerWithThePortsOfTheirL2()
    {
        var store = new AmdStore(Repository.Amds); store.Scan();
        var controller = new Controller(store);
        foreach (var aim in Aims)
        {
            var graph = controller.RegisterAim(store.FindByAimName(aim) ?? throw new InvalidOperationException(aim));
            var l2 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Schemas, "XRV1", "V1.0", "AIMs", L2File(aim))))!["ExternalPorts"]!.AsArray();
            var loaded = graph.Root.Ports;
            Assert.Equal(l2.Count, loaded.Count);
            foreach (var p in l2)
            {
                var direction = p!["Direction"]!.GetValue<string>(); var type = p["DataType"]!.GetValue<string>(); var number = p["PortNumber"]?.GetValue<int>() ?? 1;
                var port = loaded.Single(x => x.Direction == direction && x.DataType == type && (x.PortNumber ?? 1) == number);
                Assert.Equal(p["IsOptional"]?.GetValue<bool>() ?? false, port.IsOptional);
                Assert.Equal(p["Depth"]?.GetValue<int>(), port.Depth);                           // Depth 1 on the Cue Point ID, as the L2 says
                Assert.Equal(p["Overflow"]?.GetValue<string>(), port.Overflow);                  // DropOldest on it and on the Descriptors
            }
        }
    }

    private static string L2File(string aim) => aim[5..8] switch
    {
        "ACG" => "ActionGeneration.json", "PFI" => "PerformanceStatusInterpretation.json", "PTI" => "ParticipantStatusInterpretation.json", _ => "OperatorStatusInterpretation.json"
    };

    [Fact]
    public void TheCuePointIdPortHasDepthOneAndDropOldestAndTheDescriptorsDropOldest()
    {
        foreach (var aim in Aims.Skip(1))
        {
            var l3 = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, aim + ".json")))!["ExternalPorts"]!.AsArray();
            var cue = l3.Single(p => p!["DataType"]!.GetValue<string>() == "OSD-BTO-V1.5")!;
            Assert.Equal(1, cue["Depth"]!.GetValue<int>()); Assert.Equal("DropOldest", cue["Overflow"]!.GetValue<string>());
            foreach (var d in l3.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p!["DataType"]!.GetValue<string>(), @"^XRV-[RV][FTO]D-V1\.0$")))     // XRV-xxD: the Descriptors
                Assert.Equal("DropOldest", d!["Overflow"]!.GetValue<string>());
        }
        var acg = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository.Amds, Aims[0] + ".json")))!["ExternalPorts"]!.AsArray();
        Assert.Equal(new[] { 1, 2, 3 }, acg.Where(p => p!["Direction"]!.GetValue<string>() == "Output" && p["DataType"]!.GetValue<string>() == "OSD-BTO-V1.5").Select(p => p!["PortNumber"]!.GetValue<int>()).ToArray());
    }
}
