using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aif.Tests;

// THE FEATURES OF AN OBJECT, AND WHAT THE LIST SAYS (the author, 2026/10/26).
//
// ChangedObjects edits a defined list of FEATURES of an Object, each by a value; the schema lists them by name, so that nothing else is allowed.
//   Audio Object:        Name, Time, SpaceTime, UserPoV, ParentAudioObjectIDs, DescrMetadata, and for each child, by its name, its SpaceTime inside the parent
//   Basic Audio Object:  Name, Time, UserPoV, DeviceID, Level (LUFS), PerceptStatus, AcousticProfile, AudioQualifier, DescrMetadata
// The sound itself is not a feature. Every edit is a new version; a new name is the Object's new name (the same Object, the name unique: "violin 2"), and revoking it puts
// the old name back. A value that is not what the feature takes, a feature that is not one of the Object's kind, and a name that is no feature are reported Failed.
//
// The List of an Object also carries its Kind, Duration, ChildCount and Version. Listing a Scene by name gives the number of its members and, for each, its name and its PoV -
// the position and orientation the Scene gives it - without touching an Object. The Position of the schema states its coordinates.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AsmFeaturesTests
{
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> All = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);
    private const string Schema = "CAE3/V1.0/data/UserCommand.json";

    private static bool Valid(string schema, string json)
    {
        var s = All[Path.GetFullPath(Path.Combine(Repository.Schemas, schema))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid;
    }

    private static ObjectChanges Changes(params ObjectChange[] objects) => new() { Objects = objects.ToList() };
    // A Space/Time that is one: a place and a time.
    private static SpaceTime Where(string id, double x) => new() { SpaceTimeID = id, SpatialAttitude1 = AsmRig.At(x), Time = SimpleTime.At(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero)) };

    private sealed class Aoe(AsmRig rig)
    {
        public async Task<(Message Message, UserCommandRS Rs, string Json)> Run(UserCommandRQ rq)
        {
            var json = AsmRig.Json(AsmRig.Command(rq));
            Assert.True(Valid(Schema, json), "the command is not valid against its schema: " + json);
            var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), json));
            var report = said.Ports[rig.AoePorts.Output(AsmRig.Ucm)];
            Assert.True(Valid(Schema, report), report);
            return (said, AsmRig.Response(report), report);
        }
        public async Task<(Message Message, UserCommandRS Rs)> Raw(string json)
        {
            var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), json));
            return (said, AsmRig.Response(said.Ports[rig.AoePorts.Output(AsmRig.Ucm)]));
        }
        public async Task<Message> Open(BasicAudioObject bao) => await rig.OpenAudio(bao);
        public string AuoOut => rig.AoePorts.Output(AsmRig.Auo);
        public static JsonObject Bao(Message m, string port) => JsonNode.Parse(Forms.Bao(m.Ports[port]))!.AsObject();
        public async Task<IReadOnlyList<ListedObject>> List(string kind, string? name = null)
        {
            var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = kind, ObjectID = name } }))));
            Assert.True(Valid(Schema, said.Ports[rig.AoePorts.Output(AsmRig.Ucm)]));
            return AsmRig.Response(said.Ports[rig.AoePorts.Output(AsmRig.Ucm)]).ListedObjects ?? [];
        }
    }

    // ---- a Basic Audio Object ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachFeatureOfABasicAudioObjectIsChangedByAValue()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        var violin = AsmRig.Sound("violin", 1);
        await aoe.Open(violin);
        await aoe.Open(AsmRig.Sound("drums", 0.5, 2));
        var sound = Convert.ToBase64String(violin.Data);
        string Sound(Message m) => Aoe.Bao(m, aoe.AuoOut)["BasicAudioObjectData"]![0]!["Data"]!.GetValue<string>();

        var profile = new AcousticProfile { AcousticProfileID = "violin-acp", FrequencyRange = new FrequencyRange { MinFrequencyHz = 40, MaxFrequencyHz = 16000 }, Loudness = -18 };
        var pov = new PointOfView { PointOfViewID = "behind", CartPosition = [0, -2, 1.6], Orientation = [0, 0, 0] };
        var time = SimpleTime.At(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var changes = new (string Feature, ObjectChange Change, Func<JsonObject, string> Seen, string Expected)[]
        {
            ("Level", new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -18.5 }, b => b["BasicAudioObjectProperties"]!["Level"]!.ToString(), "-18.5"),
            ("PerceptStatus", new ObjectChange { ObjectID = AsmRig.Ref("violin"), PerceptStatus = true }, b => b["BasicAudioObjectProperties"]!["PerceptStatus"]!.ToString().ToLowerInvariant(), "true"),
            ("DeviceID", new ObjectChange { ObjectID = AsmRig.Ref("violin"), DeviceID = "microphone-1" }, b => b["DeviceID"]!.ToString(), "microphone-1"),
            ("AcousticProfile", new ObjectChange { ObjectID = AsmRig.Ref("violin"), AcousticProfile = profile }, b => b["BasicAudioObjectProperties"]!["AcousticProfile"]!["AcousticProfileID"]!.ToString(), "violin-acp"),
            ("AudioQualifier", new ObjectChange { ObjectID = AsmRig.Ref("violin"), AudioQualifier = new AudioQualifier { AudioQualifierID = "violin-q", SubTypes = "Music", Formats = new AudioFormats() } }, b => b["AudioQualifier"]!["AudioQualifierID"]!.ToString(), "violin-q"),
            ("DescrMetadata", new ObjectChange { ObjectID = AsmRig.Ref("violin"), DescrMetadata = "a solo violin" }, b => b["DescrMetadata"]!.ToString(), "a solo violin"),
            ("UserPoV", new ObjectChange { ObjectID = AsmRig.Ref("violin"), UserPoV = pov }, b => b["UserPoV"]!["PointOfViewID"]!.ToString(), "behind"),
            ("Time", new ObjectChange { ObjectID = AsmRig.Ref("violin"), Time = time }, b => b["BasicAudioObjectTime"]!["Time"]!["SimpleTimeID"]!.ToString(), time.SimpleTimeID)
        };

        var version = 1;
        foreach (var (feature, change, seen, expected) in changes)
        {
            var (said, rs, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(change) });
            Assert.True(rs.Outcome == "Done", $"{feature}: {rs.Outcome} {rs.Actions.FirstOrDefault()?.Reason}");
            Assert.Equal("ChangedObjects", rs.Actions[0].Action);
            Assert.Equal("violin", rs.Actions[0].ObjectID);
            Assert.Equal("violin", rs.Actions[0].ResultID);                                           // a new version has the name of the Object
            Assert.Equal(expected, seen(Aoe.Bao(said, aoe.AuoOut)));
            Assert.Equal(sound, Sound(said));                                                        // the sound is not a feature: not edited
            Assert.Equal(++version, (await aoe.List("Basic Audio Object", "violin")).Single().Version);   // every edit is a new version
        }
        Assert.Equal(1, (await aoe.List("Basic Audio Object", "drums")).Single().Version);

        // Several features in one command, for one Object: one new version.
        var (_, many, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("drums"), Level = -9, DeviceID = "kit", DescrMetadata = "drums" }) });
        Assert.Equal("Done", many.Outcome);
        Assert.Equal(2, (await aoe.List("Basic Audio Object", "drums")).Single().Version);

        // The earlier changes are kept when a later one is made.
        var kept = Aoe.Bao((await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -14 }) })).Message, aoe.AuoOut);
        Assert.Equal("-14", kept["BasicAudioObjectProperties"]!["Level"]!.ToString());
        Assert.Equal("microphone-1", kept["DeviceID"]!.ToString());
        Assert.Equal("violin-q", kept["AudioQualifier"]!["AudioQualifierID"]!.ToString());
        Assert.Equal("violin-acp", kept["BasicAudioObjectProperties"]!["AcousticProfile"]!["AcousticProfileID"]!.ToString());
    }

    [Fact]
    public async Task ANameThatIsNoFeatureAndAValueThatIsNoNumberAreRejected()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        await aoe.Open(AsmRig.Sound("violin"));
        string Changed(Action<JsonObject> edit)
        {
            var command = JsonNode.Parse(AsmRig.Json(AsmRig.Command(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -10 }) })))!.AsObject();
            edit(command["UserCommandRQ"]!["ChangedObjects"]!["Objects"]![0]!.AsObject());
            return command.ToJsonString();
        }

        // A name that is no feature: the schema does not allow it, and the AIM reports it - the feature that is one is not applied either way, the Object is as it was.
        var colour = Changed(o => o["Colour"] = "red");
        Assert.False(Valid(Schema, colour));
        var (said, rs) = await aoe.Raw(colour);
        Assert.Equal("Failed", rs.Outcome);
        Assert.Contains("Colour", rs.Actions[0].Reason);
        Assert.Equal(1, (await aoe.List("Basic Audio Object", "violin")).Single().Version);
        Assert.True(Valid(Schema, said.Ports[rig.AoePorts.Output(AsmRig.Ucm)]));
        var sound = Changed(o => o["Data"] = "AAAA");                                               // the sound is not a feature
        Assert.False(Valid(Schema, sound));
        Assert.Equal("Failed", (await aoe.Raw(sound)).Rs.Outcome);
        var two = Changed(o => { o["Name"] = "fiddle"; o["Tempo"] = 120; });
        Assert.False(Valid(Schema, two));
        Assert.Equal("Failed", (await aoe.Raw(two)).Rs.Outcome);
        Assert.Single(await aoe.List("Basic Audio Object", "violin"));                                // not renamed

        // An amount is a number: a word is not a loudness, and the command is reported, not guessed.
        var word = Changed(o => o["Level"] = "a bit louder");
        Assert.False(Valid(Schema, word));
        var (_, notANumber) = await aoe.Raw(word);
        Assert.Equal("Failed", notANumber.Outcome);
        Assert.Equal("ChangedObjects", notANumber.Actions[0].Action);
        Assert.Contains("number", notANumber.Actions[0].Reason);
        var flag = Changed(o => { o.Remove("Level"); o["PerceptStatus"] = "yes"; });
        Assert.False(Valid(Schema, flag));
        Assert.Equal("Failed", (await aoe.Raw(flag)).Rs.Outcome);

        // An entry with no feature, no Object named, an Object the Module does not keep, and the place of an Object in a Scene (not Audio Object Editing's).
        Assert.False(Valid(Schema, Changed(o => o.Remove("Level"))));
        Assert.Equal("Failed", (await aoe.Raw(Changed(o => o.Remove("Level")))).Rs.Outcome);
        var unnamed = Changed(o => o.Remove("ObjectID"));
        Assert.False(Valid(Schema, unnamed));
        Assert.Equal("Failed", (await aoe.Raw(unnamed)).Rs.Outcome);
        Assert.Equal("Failed", (await aoe.Raw(Changed(o => o["ObjectID"] = "tuba"))).Rs.Outcome);
        var place = (await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), SpatialAttitude = AsmRig.At(1) }) })).Rs;
        Assert.Equal("NotSupported", place.Actions[0].Outcome);
        Assert.Contains("Scene", place.Actions[0].Reason);
        Assert.Equal(1, (await aoe.List("Basic Audio Object", "violin")).Single().Version);

        // A feature of the other kind: the Level is a Basic Audio Object's, the SpaceTime an Audio Object's.
        await aoe.Open(AsmRig.Sound("drums", 1, 2));
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "band", Objects = [AsmRig.Put("violin", 0), AsmRig.Put("drums", 2)] } }))));
        var onAudio = (await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("band"), Level = -10 }) })).Rs;
        Assert.Equal("Failed", onAudio.Outcome);
        Assert.Contains("Level", onAudio.Actions[0].Reason);
        var onBasic = (await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), SpaceTime = new SpaceTime() }) })).Rs;
        Assert.Equal("Failed", onBasic.Outcome);
        Assert.Contains("SpaceTime", onBasic.Actions[0].Reason);
    }

    // ---- the name ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARenameIsTheSameObjectUnderANewNameThatStaysUnique()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        await aoe.Open(AsmRig.Sound("violin", 1));
        await aoe.Open(AsmRig.Sound("drums", 0.5, 2));
        await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -12 }) });      // violin is at version 2

        var (said, rs, report) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), Name = "fiddle" }) });
        Assert.Equal("Done", rs.Outcome);
        Assert.Equal("violin", rs.Actions[0].ObjectID);
        Assert.Equal("fiddle", rs.Actions[0].ResultID);
        Assert.Equal("fiddle", Aoe.Bao(said, aoe.AuoOut)["BasicAudioObjectID"]!.ToString());              // BasicAudioObjectID is the new name
        Assert.Equal("-12", Aoe.Bao(said, aoe.AuoOut)["BasicAudioObjectProperties"]!["Level"]!.ToString());    // the same Object: what it was is kept
        var listed = await aoe.List("All");
        Assert.Equal(["drums", "fiddle"], listed.Select(o => o.ObjectID));                                 // the old name designates nothing
        Assert.Equal(3, listed.Single(o => o.ObjectID == "fiddle").Version);                              // a new version of the same Object
        Assert.Equal("Failed", (await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("violin"), Level = -3 }) })).Rs.Outcome);

        // The name stays unique: a name that is taken becomes the name and the next free number.
        var (taken, takenRs, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("drums"), Name = "fiddle" }) });
        Assert.Equal("Done", takenRs.Outcome);
        Assert.Equal("fiddle 2", takenRs.Actions[0].ResultID);
        Assert.Equal("fiddle 2", Aoe.Bao(taken, aoe.AuoOut)["BasicAudioObjectID"]!.ToString());
        Assert.Equal(["fiddle", "fiddle 2"], (await aoe.List("All")).Select(o => o.ObjectID));
        // The same name again is the Object's own: nothing to rename.
        var (_, same, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("fiddle 2"), Name = "fiddle 2" }) });
        Assert.Equal("Done", same.Outcome);
        Assert.Equal(["fiddle", "fiddle 2"], (await aoe.List("All")).Select(o => o.ObjectID));
        // An empty name is no name.
        Assert.Equal("Failed", (await aoe.Raw(AsmRig.Json(AsmRig.Command(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("fiddle"), Name = "  " }) })))).Rs.Outcome);

        // Revoked, the command puts the old name back and the new one designates nothing.
        var undo = AsmRig.Command(new UserCommandRQ { RevokedUserCommandID = JsonNode.Parse(report)!["UserCommandID"]!.GetValue<string>() });
        var tooEarly = AsmRig.Response((await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(undo)))).Ports[rig.AoePorts.Output(AsmRig.Ucm)]);
        Assert.Equal("Failed", tooEarly.Outcome);                                                         // later commands were executed after it: revoke them first

        // A rename revoked at once.
        var renamed = AsmRig.Command(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("fiddle 2"), Name = "snare" }) });
        Assert.Equal("Done", AsmRig.Response((await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(renamed)))).Ports[rig.AoePorts.Output(AsmRig.Ucm)]).Outcome);
        Assert.Equal(["fiddle", "snare"], (await aoe.List("All")).Select(o => o.ObjectID));
        var revoke = AsmRig.Command(new UserCommandRQ { RevokedUserCommandID = renamed.UserCommandID });
        var revoked = AsmRig.Response((await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(revoke)))).Ports[rig.AoePorts.Output(AsmRig.Ucm)]);
        Assert.Equal("Done", revoked.Outcome);
        Assert.Equal(["fiddle", "fiddle 2"], (await aoe.List("All")).Select(o => o.ObjectID));
    }

    // ---- an Audio Object ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EachFeatureOfAnAudioObjectIsChangedByAValueAndItsChildrenAreNamed()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        await aoe.Open(AsmRig.Sound("violin", 1));
        await aoe.Open(AsmRig.Sound("drums", 0.5, 2));
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "band", Objects = [AsmRig.Put("violin", 0), AsmRig.Put("drums", 2)] } }))));
        Assert.Equal(1, (await aoe.List("Audio Object", "band")).Single().Version);

        static JsonObject Auo(Message m, string port) => JsonNode.Parse(m.Ports[port])!.AsObject();
        static double X(JsonNode? spaceTime) => spaceTime!["SpatialAttitude1"]!["Position"]!["CartPosition"]![0]!.GetValue<double>();
        var place = Where("in-band", 7);
        var time = SimpleTime.At(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        var pov = new PointOfView { PointOfViewID = "front", CartPosition = [0, 3, 1.6], Orientation = [0, 0, 180] };

        var (said, rs, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("band"), SpaceTime = place, Time = time, UserPoV = pov, ParentAudioObjectIDs = ["orchestra"], DescrMetadata = "the band" }) });
        Assert.Equal("Done", rs.Outcome);
        var band = Auo(said, aoe.AuoOut);
        Assert.Equal("band", band["AudioObjectID"]!.ToString());
        Assert.Equal("in-band", band["AudioObjectSpaceTime"]!["SpaceTimeID"]!.ToString());
        Assert.Equal(time.SimpleTimeID, band["AudioObjectTime"]!["SimpleTimeID"]!.ToString());
        Assert.Equal("front", band["UserPoV"]!["PointOfViewID"]!.ToString());
        Assert.Equal("orchestra", band["ParentAudioObjectIDs"]![0]!.ToString());
        Assert.Equal("the band", band["DescrMetadata"]!.ToString());
        Assert.Equal(2, (await aoe.List("Audio Object", "band")).Single().Version);

        // A child, by its name, is placed inside the parent: only that child's SpaceTime changes.
        var before = Auo(said, aoe.AuoOut)["BasicAudioObjects"]!.AsArray().Select(e => X(e!["BasicAudioObjectSpaceTime"])).ToList();
        var (moved, movedRs, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("band"), Children = [new ChildSpaceTime { ObjectID = "drums", SpaceTime = Where("drums-in-band", 5) }] }) });
        Assert.Equal("Done", movedRs.Outcome);
        var after = Auo(moved, aoe.AuoOut)["BasicAudioObjects"]!.AsArray().Select(e => X(e!["BasicAudioObjectSpaceTime"])).ToList();
        Assert.Equal([0.0, 2.0], before);
        Assert.Equal([0.0, 5.0], after);
        Assert.Equal("in-band", Auo(moved, aoe.AuoOut)["AudioObjectSpaceTime"]!["SpaceTimeID"]!.ToString());     // what was not changed is kept

        // A child that is not one.
        var noChild = (await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("band"), Children = [new ChildSpaceTime { ObjectID = "tuba", SpaceTime = place }] }) })).Rs;
        Assert.Equal("Failed", noChild.Outcome);
        Assert.Contains("tuba", noChild.Actions[0].Reason);
        Assert.Equal(3, (await aoe.List("Audio Object", "band")).Single().Version);

        // The name of an Audio Object is its AudioObjectID: renamed, its children are still its children.
        var (renamed, renamedRs, _) = await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("band"), Name = "ensemble" }) });
        Assert.Equal("Done", renamedRs.Outcome);
        Assert.Equal("ensemble", Auo(renamed, aoe.AuoOut)["AudioObjectID"]!.ToString());
        Assert.Equal(["violin", "drums"], Auo(renamed, aoe.AuoOut)["BasicAudioObjects"]!.AsArray().Select(e => e!["BAObjectIDOrBAObject"]![0]!["BasicAudioObjectID"]!.ToString()));
        Assert.Equal(["drums", "ensemble", "violin"], (await aoe.List("All")).Select(o => o.ObjectID));
        Assert.Equal(4, (await aoe.List("Audio Object", "ensemble")).Single().Version);
    }

    // ---- the List: attributes of an Object ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListOfAnObjectSaysItsKindDurationChildrenAndVersion()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        await aoe.Open(AsmRig.Sound("violin", 1));
        await aoe.Open(AsmRig.Sound("drums", 0.5, 2));
        await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "band", Objects = [AsmRig.Put("violin", 0), AsmRig.Put("drums", 2)] } }))));
        await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("drums"), Level = -10 }) });
        await aoe.Run(new UserCommandRQ { ChangedObjects = Changes(new ObjectChange { ObjectID = AsmRig.Ref("drums"), Level = -11 }) });

        var all = await aoe.List("All");
        string Line(ListedObject o) => $"{o.ObjectID}|{o.Kind}|{o.Duration}|{o.ChildCount}|{o.Version}";
        Assert.Equal(["band|Audio Object|1|2|1", "drums|Basic Audio Object|0.5|0|3", "violin|Basic Audio Object|1|0|1"], all.Select(Line));

        // Over the wire, in the response, valid; and it says nothing of a key.
        var said = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } }))));
        var json = said.Ports[rig.AoePorts.Output(AsmRig.Ucm)];
        Assert.True(Valid(Schema, json), json);
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(json, "\"(BAO|AUO)\\d{6}\""));
        var entry = JsonNode.Parse(json)!["UserCommandRS"]!["ListedObjects"]![1]!;
        Assert.Equal(0.5, entry["Duration"]!.GetValue<double>());
        Assert.Equal(3, entry["Version"]!.GetValue<int>());
        Assert.Equal(0, entry["ChildCount"]!.GetValue<int>());

        // A name that is not kept, and ObjectID with All (which is not one thing): failed.
        var nobody = await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Basic Audio Object", ObjectID = "tuba" } }))));
        Assert.Equal("Failed", AsmRig.Response(nobody.Ports[rig.AoePorts.Output(AsmRig.Ucm)]).Outcome);
        var every = AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All", ObjectID = "violin" } });
        Assert.False(Valid(Schema, AsmRig.Json(every)));
        Assert.Equal("Failed", AsmRig.Response((await rig.Give(rig.Aoe, (rig.AoePorts.Input(AsmRig.Ucm), AsmRig.Json(every)))).Ports[rig.AoePorts.Output(AsmRig.Ucm)]).Outcome);

        // Speech Object Editing says the same of a Speech Object.
        await rig.OpenSpeech(AsmRig.Speech("hello", 0.5));
        var spoken = await rig.Give(rig.Soe, (rig.SoePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } }))));
        var hello = AsmRig.Response(spoken.Ports[rig.SoePorts.Output(AsmRig.Ucm)]).ListedObjects!.Single();
        Assert.Equal(0.5, hello.Duration);
        Assert.Equal(0, hello.ChildCount);
        Assert.Equal(1, hello.Version);
    }

    // ---- the List: a Scene ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASceneIsListedWithItsMembersAndTheirPoV()
    {
        using var rig = new AsmRig();
        var aoe = new Aoe(rig);
        await aoe.Open(AsmRig.Sound("violin", 1));
        await aoe.Open(AsmRig.Sound("drums", 0.5, 2));
        await rig.OpenSpeech(AsmRig.Speech("hello"));
        var stage = await rig.Give(rig.Ase, (rig.AsePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "stage", Objects = [AsmRig.Put("violin", 1, 2), AsmRig.Put("drums", 3, 4)] } }))));
        var dialogue = await rig.Give(rig.Sse, (rig.SsePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "dialogue", Objects = [AsmRig.Put("hello", 0, 5)] } }))));
        await rig.Give(rig.Mse, (rig.MsePorts.Input(AsmRig.Asd, 1), stage.Ports[rig.AsePorts.Output(AsmRig.Asd)]), (rig.MsePorts.Input(AsmRig.Asd, 2), dialogue.Ports[rig.SsePorts.Output(AsmRig.Ssd)]));
        await rig.Give(rig.Mse, (rig.MsePorts.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { AddedObjects = new ObjectPlacements { ObjectID = "mix", Objects = [AsmRig.Put("stage", 0, 0), AsmRig.Put("dialogue", 1, 1)] } }))));

        async Task<(ListedObject? Entry, UserCommandRS Rs, string Json)> ListScene(IAimProcessor aim, AimPortReader ports, string kind, string? name)
        {
            var said = await rig.Give(aim, (ports.Input(AsmRig.Ucm), AsmRig.Json(AsmRig.Command(new UserCommandRQ { ListedObjects = new ObjectListing { Kind = kind, ObjectID = name } }))));
            var json = said.Ports[ports.Output(AsmRig.Ucm)];
            Assert.True(Valid(Schema, json), json);
            return (AsmRig.Response(json).ListedObjects?.FirstOrDefault(), AsmRig.Response(json), json);
        }
        static string At(ListedMember m) =>
            $"{m.ObjectID} at {string.Join(",", m.PoV!.Position!.CartPosition!)} facing {string.Join(",", m.PoV.Orientation!.EulerAngles!)}";

        // An Audio Scene: the number of its members, and for each its name and its position and orientation as the Scene gives them.
        var (audio, _, audioJson) = await ListScene(rig.Ase, rig.AsePorts, "Audio Scene", "stage");
        Assert.Equal("stage", audio!.ObjectID);
        Assert.Equal(2, audio.ObjectCount);
        Assert.Equal(["violin at 1,2,0 facing 0,0,180", "drums at 3,4,0 facing 0,0,180"], audio.Members!.Select(At));
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(audioJson, "\"(BAO|AUO|BAS|ASD)\\d{6}\""));         // never keys

        // A Speech Scene, and a Multimodal Scene: the same.
        var (speech, _, _) = await ListScene(rig.Sse, rig.SsePorts, "Speech Scene", "dialogue");
        Assert.Equal(1, speech!.ObjectCount);
        Assert.Equal(["hello at 0,5,0 facing 0,0,180"], speech.Members!.Select(At));
        var (mix, _, mixJson) = await ListScene(rig.Mse, rig.MsePorts, "Multimodal Scene", "mix");
        Assert.Equal(2, mix!.ObjectCount);
        Assert.Equal(["stage at 0,0,0 facing 0,0,180", "dialogue at 1,1,0 facing 0,0,180"], mix.Members!.Select(At));
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(mixJson, "\"(BAS|BSS|MSD|ASD|SSD)\\d{6}\""));

        // Without the name, the Scenes are listed as before: by name, with their members' names, and no count nor PoV.
        var (plain, _, _) = await ListScene(rig.Ase, rig.AsePorts, "Audio Scene", null);
        Assert.Equal(["violin", "drums"], plain!.MemberObjectIDs);
        Assert.Null(plain.ObjectCount);
        Assert.Null(plain.Members);

        // A Scene that is not kept, and a Scene of another Kind than the AIM keeps, are reported.
        Assert.Equal("Failed", (await ListScene(rig.Ase, rig.AsePorts, "Audio Scene", "pit")).Rs.Outcome);
        Assert.Equal("Failed", (await ListScene(rig.Sse, rig.SsePorts, "Speech Scene", "stage")).Rs.Outcome);
        Assert.Equal("Failed", (await ListScene(rig.Ase, rig.AsePorts, "Speech Scene", "dialogue")).Rs.Outcome);
        var members = JsonNode.Parse(mixJson)!["UserCommandRS"]!["ListedObjects"]![0]!["Members"]!;
        Assert.False(members.AsArray().Any(m => m!["PoV"] is null));
    }

    // ---- the Position of the schema states its coordinates --------------------------------------------------------------------------------------

    [Fact]
    public void APositionStatesItsCoordinates()
    {
        const string schema = "OSD/V1.5/data/Position.json";
        Assert.True(Valid(schema, """{ "Header": "OSD-OPS-V1.5", "PositionID": "p", "CartPosition": [1, 2, 3] }"""));
        Assert.True(Valid(schema, """{ "Header": "OSD-OPS-V1.5", "PositionID": "p", "SpherPosition": [2, 30, 45] }"""));
        Assert.False(Valid(schema, """{ "Header": "OSD-OPS-V1.5", "PositionID": "p" }"""));
        Assert.False(Valid(schema, """{ "Header": "OSD-OPS-V1.5", "PositionID": "p", "CartVelocity": [1, 2, 3] }"""));
        Assert.False(Valid(schema, """{ "Header": "OSD-OPS-V1.5", "PositionID": "p", "CartPosition": [1, 2] }"""));
        // What an AIM puts in a placement is such a Position, and an AIM that finds fewer coordinates rejects the placement.
        Assert.True(Valid(schema, MpaiJson.ToJson(AsmRig.At(1).Position!)));
        Assert.Null(Amounts.PlaceProblem(AsmRig.At(1)));
        var fewer = new SpatialAttitude { ObjectSpatialAttitudeID = "a", Position = new Position { PositionID = "p", CartPosition = [1, 2] }, Orientation = new Orientation { OrientationID = "o", EulerAngles = [0, 0, 0] } };
        Assert.NotNull(Amounts.PlaceProblem(fewer));
        var none = new SpatialAttitude { ObjectSpatialAttitudeID = "a", Position = new Position { PositionID = "p" }, Orientation = new Orientation { OrientationID = "o", EulerAngles = [0, 0, 0] } };
        Assert.NotNull(Amounts.PlaceProblem(none));
        Assert.False(Valid(schema, MpaiJson.ToJson(none.Position!)));
    }
}
