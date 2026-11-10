using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AIF.SharedStorage;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Repository;

namespace Mpai.Cae.Ase;

// ---------------------------------------------------------------------------
//  CAE-ASE-V1.0 - Audio Scene Editing.
//
//  WHERE THE SCENES ARE (the author, 2026/11/08): every version of a Scene is kept in the Private Storage of the Module at OSD-ASD-V1.5#1/<the Scene's own ID>@<version> by
//  ModuleStore (the highest version is in force), and the Scene the Module gives is also offered in its Shared Storage at <Module>/OSD-ASD-V1.5#1/<ID>. No table, no counter.
//
//  A SCENE IS KNOWN BY ITS NAME (the author, 2026/10/26), as an Object is: the Scene as
//  stored, as output and as a User Command names it carries the name the User gave it in
//  its identifier (BasicAudioSceneDescriptorsID): "concert". The Scene is made of
//  Basic Audio Objects, which it holds by their IDs - their names - and tells by name. A name is unique among the Scenes; a second Scene given a name that is
//  taken is "concert 2". A Scene with no name is not created.
//
//  The Scene the Module edits is a BasicAudioSceneDescriptors (BAS) composing
//  BasicAudioObjects directly - the genuinely simple case, not an artificially-wrapped
//  AudioObject standing in for one. It leaves the Module inside an Audio Scene of one
//  sub-scene (AseAimProcessor).
// ---------------------------------------------------------------------------
public sealed class AseAim
{
    private readonly ModuleStore store;

    // NO AoeAim HERE. This class used to hold one and call it to expand each child object of a scene - one AIM invoking another directly, with no Controller between them.
    // The Objects are read from the Private Storage of the Module, where Audio Object Editing keeps them (OSD-BAO-V1.5#1, OSD-AUO-V1.5#1).
    // The M-Instance or U-Environment the Scenes are made in: OSD requires the first
    // on a Basic Audio Scene. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    // The Acoustic Profiles the Module keeps, by their AcousticProfileID (M3282 UCM-4).
    public KeptProfiles Profiles { get; }

    public AseAim(ModuleStore store, string mInstanceId = "ASM")
    {
        this.store = store;
        this.mInstanceId = mInstanceId;
        Profiles = new KeptProfiles(store);
        store.Changed += Offer;
    }

    // The Audio Scene the Module gives, in its Shared Storage: the version in force, with its Objects in it. One that is gone is withdrawn.
    private void Offer(StoredKind kind, string id)
    {
        if (kind != Kinds.AudioScene) return;
        store.Offer(Kinds.AudioScene, id, store.Holds(Kinds.AudioScene, id) ? Serialize(AsAudioScene(MaterializeBasicScene(id))) : null);
    }

    // What a Scene of one sub-scene, the Basic Scene, gives as the Audio Scene (OSD-ASD): the Scene is the one sub-scene, which stands at the origin of the Audio Scene's frame.
    public static AudioSceneDescriptors AsAudioScene(BasicAudioSceneDescriptors basic) => new()
    {
        MInstanceID = basic.MInstanceID, UEnvironmentID = basic.UEnvironmentID,
        AudioSceneDescriptorsID = basic.BasicAudioSceneDescriptorsID,
        AudioSceneDescriptorsTime = basic.BasicAudioSceneDescriptorsTime,
        AudioSceneDescriptorsSpaceTime = basic.BASSpaceTime ?? new SpaceTime(),
        UserPoV = basic.UserPoV,
        SubAudioSceneCount = 1,
        SubAudioScenes = [new SubAudioSceneEntry { BasicScene = basic }]
    };

    // What a User Command wrote, to be undone (the last first); the AIM asks for it when the command is done.
    public void ForgetSteps() => store.ForgetSteps();
    public (Func<bool> Unchanged, Action Undo)? TakeSteps() => store.TakeSteps();

    // A Scene not yet placed is at the origin of its space.
    private static SpaceTime Origin() => new()
    {
        SpaceTimeID = Guid.NewGuid().ToString(),
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [0, 0, 0] },
            Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, 0] }
        }
    };

    private static readonly JsonSerializerOptions DataOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DataOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, DataOptions)!;

    // ---- names ----

    // The name of the Basic Scene the Module keeps under it (its ID); null when it keeps no Audio Scene of that name.
    public string? KeptScene(string? name) => NameBook.Clean(name) is { } clean && store.Holds(Kinds.AudioScene, clean) ? clean : null;

    // The name of the Audio Object (Basic or Full) that Audio Object Editing keeps under a name; null when none.
    public string? KeptObject(string? name) => NameBook.Clean(name) is { } clean && (store.Holds(Kinds.BasicAudioObject, clean) || store.Holds(Kinds.AudioObject, clean)) ? clean : null;

    // THE OPEN SCENE IS GIVEN A NAME (the author, 2026/10/28): from now on it is kept under that name, and under no other; its earlier versions keep the name they had. The name must be free.
    // Null when it was done, else why not.
    public string? RenameScene(string id, string? newName)
    {
        if (NameBook.Clean(newName) is not { } clean) return "a name is needed";
        if (store.Holds(Kinds.AudioScene, clean)) return "a Scene of that name is kept";
        var scene = Existing(id);
        store.Move(Kinds.AudioScene, id, clean, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = scene.MInstanceID, BasicAudioSceneDescriptorsID = clean, BasicAudioSceneDescriptorsTime = scene.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = scene.BASSpaceTime, UserPoV = scene.UserPoV, ClosedSpace = scene.ClosedSpace, AcousticProfile = scene.AcousticProfile,
            RenderingMode = scene.RenderingMode, ChannelCount = scene.ChannelCount,
            AudioObjectCount = scene.AudioObjectCount, BasicAudioSceneDescriptorsEntries = scene.BasicAudioSceneDescriptorsEntries
        }));
        return null;
    }

    public bool IsBasicObject(string? id) => store.Holds(Kinds.BasicAudioObject, id);
    public bool IsFullObject(string? id) => store.Holds(Kinds.AudioObject, id);

    // THE BASIC AUDIO OBJECTS OF A FULL AUDIO OBJECT (the author, 2026/11/08: everything in ASM is Full, a Basic Object is the case of one part): the Basic Audio Objects it holds, at any depth
    // (its sub-Objects too), each with where it stands in the frame the Object is placed in - the placement of the Object composed with its own. A Basic Audio Object is its own leaf.
    // A Basic Audio Scene holds Basic Audio Objects: so a Full Audio Object is placed in it part by part.
    public IReadOnlyList<(string Key, SpaceTime? Placement)> Leaves(string key, SpaceTime? placement)
    {
        if (IsBasicObject(key)) return [(key, placement)];
        var leaves = new List<(string Key, SpaceTime? Placement)>();
        if (!IsFullObject(key)) return leaves;
        var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, key)!);
        foreach (var e in auo.BasicAudioObjects ?? [])
            if (e.BAObjectIDOrBAObject?.BasicAudioObjectID is { } child && IsBasicObject(child))
                leaves.Add((child, Mpai.Cae.Aoe.AoeAim.Within(placement, Mpai.Cae.Aoe.AoeAim.Within(auo.AudioObjectSpaceTime, e.BasicAudioObjectSpaceTime))));
        foreach (var e in auo.SubAudioObjects ?? [])
            if (e.SubAObjectIDOrSubAObject?.AudioObjectID is { } sub)
                leaves.AddRange(Leaves(sub, Mpai.Cae.Aoe.AoeAim.Within(placement, Mpai.Cae.Aoe.AoeAim.Within(auo.AudioObjectSpaceTime, e.SubAudioObjectSpaceTime))));
        return leaves;
    }

    private static BasicAudioSceneEntry MakeBasicEntry(string basicAudioObjectKey, SpaceTime? placement) => new()
    {
        AudioObjectIDOrAudioObject = new BasicAudioObject { BasicAudioObjectID = basicAudioObjectKey },
        AudioObjectSpaceTime = placement
    };

    // ---- creating and editing -----------------------------------------------

    // Wraps a single BasicAudioObject into a new, minimal Basic scene under the name given (never one made up). listenerPointOfView is required -
    // BasicAudioSceneDescriptors.json lists it as a required field.
    public RepositoryAsset CreateBasicScene(string basicAudioObjectKey, PointOfView listenerPointOfView, SpaceTime? placement, string? sceneName)
    {
        if (!IsBasicObject(basicAudioObjectKey))
            throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");
        if (NameBook.Clean(sceneName) is null) throw new NameNeededException("the Audio Scene");

        var name = NameBook.Free(store, sceneName, Kinds.AudioScene);
        store.Write(Kinds.AudioScene, name, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = name,
            BASSpaceTime = Origin(),
            UserPoV = listenerPointOfView,
            AudioObjectCount = 1,
            BasicAudioSceneDescriptorsEntries = new() { MakeBasicEntry(basicAudioObjectKey, placement) }
        }));

        return new RepositoryAsset { AssetId = name, AssetType = AssetType.BAS };
    }

    // A Scene with nothing in it yet, under the name given (a room can be built before any sound is put in it).
    public RepositoryAsset CreateEmptyBasicScene(PointOfView listenerPointOfView, string? sceneName)
    {
        if (NameBook.Clean(sceneName) is null) throw new NameNeededException("the Audio Scene");
        var name = NameBook.Free(store, sceneName, Kinds.AudioScene);
        store.Write(Kinds.AudioScene, name, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId, BasicAudioSceneDescriptorsID = name, BASSpaceTime = Origin(), UserPoV = listenerPointOfView,
            AudioObjectCount = 0, BasicAudioSceneDescriptorsEntries = new()
        }));
        return new RepositoryAsset { AssetId = name, AssetType = AssetType.BAS };
    }

    // Adds an existing BasicAudioObject as a further placement in an existing Basic scene. Produces a NEW key (no-cascade Save rule).
    public RepositoryAsset AddObjectToBasicScene(string sceneKey, string basicAudioObjectKey, SpaceTime? placement = null)
    {
        var existing = Existing(sceneKey);
        if (!IsBasicObject(basicAudioObjectKey))
            throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");

        var entries = existing.BasicAudioSceneDescriptorsEntries.ToList();
        entries.Add(MakeBasicEntry(basicAudioObjectKey, placement));
        return Store(sceneKey, existing, entries, existing.UserPoV, existing.ClosedSpace, existing.AcousticProfile);
    }

    // A placed member moved, or taken out: a NEW version, as every edit. The member is found by its name; null when the Scene has none.
    public RepositoryAsset? ReplaceInBasicScene(string sceneKey, string memberName, SpaceTime? placement) =>
        RebuildBasicScene(sceneKey, memberName, e => new BasicAudioSceneEntry
        {
            AudioObjectIDOrAudioObject = e.AudioObjectIDOrAudioObject, UserPoV = e.UserPoV, AudioObjectSpaceTime = Placed(placement, e.AudioObjectSpaceTime)
        });

    // A member moved keeps the time it sounds at unless the move states another: moving a sound does not make it sound from the start.
    private static SpaceTime? Placed(SpaceTime? placement, SpaceTime? before) =>
        placement is not null && placement.Time?.SimpleTimeData.Count is null or 0 && before?.Time?.SimpleTimeData.Count > 0 ? new SpaceTime { SpatialAttitude1 = placement.SpatialAttitude1, Time = before.Time } : placement ?? before;

    public RepositoryAsset? RemoveFromBasicScene(string sceneKey, string memberName) =>
        RebuildBasicScene(sceneKey, memberName, _ => null);

    private RepositoryAsset? RebuildBasicScene(string sceneKey, string memberName, Func<BasicAudioSceneEntry, BasicAudioSceneEntry?> change)
    {
        var existing = Existing(sceneKey);
        var found = false;
        var entries = new List<BasicAudioSceneEntry>();
        foreach (var e in existing.BasicAudioSceneDescriptorsEntries)
        {
            if (!found && e.AudioObjectIDOrAudioObject?.BasicAudioObjectID == memberName)
            {
                found = true;
                if (change(e) is { } kept) entries.Add(kept);
            }
            else entries.Add(e);
        }
        if (!found) return null;
        return Store(sceneKey, existing, entries, existing.UserPoV, existing.ClosedSpace, existing.AcousticProfile);
    }

    // Sets where a Basic scene is heard from: a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneListener(string sceneKey, PointOfView userPoV)
    {
        var existing = Existing(sceneKey);
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, userPoV, existing.ClosedSpace, existing.AcousticProfile);
    }

    // Sets how a Basic scene is rendered for the listener (RenderingMode of the Scene, and ChannelCount for Multichannel; the count of any other mode is dropped): a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneRendering(string sceneKey, string renderingMode, int? channelCount)
    {
        var existing = Existing(sceneKey);
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, existing.UserPoV, existing.ClosedSpace, existing.AcousticProfile, renderingMode, renderingMode == "Multichannel" ? channelCount : null, renders: true);
    }

    // Sets the acoustics of a Basic scene (the Scene part of the Acoustic Profile): a NEW version, as every edit.
    // The properties the Profile given states are set; the ones it does not state keep the value the Scene has.
    public RepositoryAsset SetBasicSceneAcousticProfile(string sceneKey, AcousticProfile acousticProfile)
    {
        var existing = Existing(sceneKey);
        var before = existing.AcousticProfile;
        var merged = before is null ? acousticProfile : new AcousticProfile
        {
            Header = acousticProfile.Header, MInstanceID = acousticProfile.MInstanceID ?? before.MInstanceID, UEnvironmentID = acousticProfile.UEnvironmentID ?? before.UEnvironmentID,
            AcousticProfileID = string.IsNullOrEmpty(acousticProfile.AcousticProfileID) ? before.AcousticProfileID : acousticProfile.AcousticProfileID,
            AcousticProfileTime = acousticProfile.AcousticProfileTime ?? before.AcousticProfileTime,
            FrequencyRange = acousticProfile.FrequencyRange ?? before.FrequencyRange, Spectrogram = acousticProfile.Spectrogram ?? before.Spectrogram,
            Loudness = acousticProfile.Loudness ?? before.Loudness, DirectionalPatterns = acousticProfile.DirectionalPatterns ?? before.DirectionalPatterns,
            Reflectivity = acousticProfile.Reflectivity ?? before.Reflectivity, Reverberation = acousticProfile.Reverberation ?? before.Reverberation,
            Diffusion = acousticProfile.Diffusion ?? before.Diffusion, Absorption = acousticProfile.Absorption ?? before.Absorption, Doppler = acousticProfile.Doppler ?? before.Doppler,
            Walls = MergedWalls(before.Walls, acousticProfile.Walls), DefaultMaterial = acousticProfile.DefaultMaterial ?? before.DefaultMaterial,
            DataXMData = acousticProfile.DataXMData ?? before.DataXMData, DescrMetadata = acousticProfile.DescrMetadata ?? before.DescrMetadata
        };
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, existing.UserPoV, existing.ClosedSpace, merged);
    }

    // The Acoustic Profile a Scene holds (null where it has none).
    public AcousticProfile? AcousticProfileOf(string sceneKey) => Existing(sceneKey).AcousticProfile;

    // Where, and when, a member of the Scene is (null when the Scene has no member of that name): a change that states only some of the parts keeps the rest of this.
    public SpaceTime? PlacementOf(string sceneKey, string memberName)
    {
        foreach (var e in Existing(sceneKey).BasicAudioSceneDescriptorsEntries)
            if (e.AudioObjectIDOrAudioObject?.BasicAudioObjectID == memberName) return e.AudioObjectSpaceTime;
        return null;
    }

    // THE LISTENER and THE ROOM of a Scene, as the list tells them (M3282 UCM-2).
    public ListedObject ListedListener(string sceneKey) =>
        new() { ObjectID = sceneKey, Kind = ListKinds.Listener, UserPoV = Existing(sceneKey).UserPoV };

    public ListedObject ListedClosedSpace(string sceneKey) =>
        new()
        {
            ObjectID = sceneKey, Kind = ListKinds.ClosedSpace,
            ClosedSpace = Existing(sceneKey).ClosedSpace is { Count: > 0 } room && room[0] is System.Text.Json.Nodes.JsonObject own ? (System.Text.Json.Nodes.JsonObject)own.DeepClone() : null
        };

    // THE WALLS GIVEN are merged into the walls the Scene has (CAE-ACP): a wall given replaces the wall with the same name - the same Right Parallelepiped, Face and Region, or the same Polygon,
    // or the same SurfaceID - and the others keep their Material: "the floor is carpet" does not undo "the ceiling is wood". None given, the walls the Scene has.
    private static System.Text.Json.Nodes.JsonArray? MergedWalls(System.Text.Json.Nodes.JsonArray? had, System.Text.Json.Nodes.JsonArray? given)
    {
        if (given is null) return had;
        static string Key(System.Text.Json.Nodes.JsonNode? w) =>
            w?["SurfaceID"] is { } surface ? "S|" + (string?)surface
            : w?["Polygon"] is { } polygon ? "P|" + polygon.ToJsonString()
            : $"F|{(string?)w?["RightParallelepipedID"]}|{(string?)w?["Face"]}|{w?["Region"]?.ToJsonString()}";
        var walls = had?.Where(w => w is not null && !given.Any(g => Key(g) == Key(w))).Select(w => w!.DeepClone()).ToList() ?? [];
        walls.AddRange(given.Where(g => g is not null).Select(g => g!.DeepClone()));
        return new System.Text.Json.Nodes.JsonArray(walls.ToArray());
    }

    // Sets the space a Basic scene is in - a room built round its members: a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneClosedSpace(string sceneKey, System.Text.Json.Nodes.JsonArray closedSpace)
    {
        var existing = Existing(sceneKey);
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, existing.UserPoV, closedSpace, existing.AcousticProfile);
    }

    private BasicAudioSceneDescriptors Existing(string sceneKey)
    {
        if (store.Get(Kinds.AudioScene, sceneKey) is not { } bytes)
            throw new InvalidOperationException("The Module keeps no such Audio Scene.");
        return Deserialize<BasicAudioSceneDescriptors>(bytes);
    }

    // The new version of a Scene: the next number under the Scene's name, which designates it from now on; the version it comes from keeps its place.
    private RepositoryAsset Store(string name, BasicAudioSceneDescriptors existing, IEnumerable<BasicAudioSceneEntry> entries, PointOfView? userPoV,
                                  System.Text.Json.Nodes.JsonArray? closedSpace, AcousticProfile? acousticProfile, string? renderingMode = null, int? channelCount = null, bool renders = false)
    {
        var kept = entries.ToList();
        store.Write(Kinds.AudioScene, name, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = name,
            BasicAudioSceneDescriptorsTime = existing.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = existing.BASSpaceTime ?? Origin(),
            UserPoV = userPoV,
            ClosedSpace = closedSpace,
            AcousticProfile = acousticProfile,
            RenderingMode = renders ? renderingMode : existing.RenderingMode, ChannelCount = renders ? channelCount : existing.ChannelCount,
            AudioObjectCount = kept.Count,
            BasicAudioSceneDescriptorsEntries = kept
        }));
        return new RepositoryAsset { AssetId = name, AssetType = AssetType.BAS };
    }

    // ---- reading --------------------------------------------------------------

    public bool Has(string? id) => store.Holds(Kinds.AudioScene, id);

    // Resolves a stored Basic scene into a fully self-contained structure: its own fields, with every ID-stub BasicAudioObject entry replaced by its full, live content
    // (which carries its name), and the Scene itself named by its name.
    public BasicAudioSceneDescriptors MaterializeBasicScene(string sceneKey)
    {
        var scene = Existing(sceneKey);
        var name = sceneKey;

        var resolvedEntries = new List<BasicAudioSceneEntry>();

        foreach (var entry in scene.BasicAudioSceneDescriptorsEntries)
        {
            var childKey = entry.AudioObjectIDOrAudioObject?.BasicAudioObjectID;
            if (string.IsNullOrEmpty(childKey) || !store.Holds(Kinds.BasicAudioObject, childKey)) continue;

            var bao = Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, childKey)!);

            resolvedEntries.Add(new BasicAudioSceneEntry
            {
                AudioObjectIDOrAudioObject = bao,
                AudioObjectSpaceTime = entry.AudioObjectSpaceTime,
                AudioSceneEnrichment = entry.AudioSceneEnrichment,
                UserPoV = entry.UserPoV
            });
        }

        return new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = name,
            BasicAudioSceneDescriptorsTime = scene.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = scene.BASSpaceTime ?? Origin(),
            UserPoV = scene.UserPoV,
            ClosedSpace = scene.ClosedSpace,
            AcousticProfile = scene.AcousticProfile,
            RenderingMode = scene.RenderingMode, ChannelCount = scene.ChannelCount,
            AudioObjectCount = resolvedEntries.Count,
            BasicAudioSceneDescriptorsEntries = resolvedEntries
        };
    }

    // THE LIST (the author, 2026/10/26): the Audio Scenes the Module keeps - the version in force of each - by name, with the names of the Objects each holds. Never a key.
    //
    // A Scene named in the request (named) is listed with the number of its members and, for each, its name and its PoV - the position and orientation the Scene gives it. No Object
    // is read to say it.
    public IReadOnlyList<ListedObject> Listing(string requested, string? named = null)
    {
        var found = new List<ListedObject>();
        if (!ListKinds.Asks(requested, ListKinds.AudioScene)) return found;
        foreach (var (name, _) in store.All(Kinds.AudioScene))
        {
            if (named is not null && name != named) continue;
            var entries = Deserialize<BasicAudioSceneDescriptors>(store.Get(Kinds.AudioScene, name)!).BasicAudioSceneDescriptorsEntries;
            var members = entries?.Select(e => e.AudioObjectIDOrAudioObject?.BasicAudioObjectID).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            double? duration = null;
            List<ListedMember>? told = null;
            if (named is not null && entries is not null)
            {
                told = [];
                foreach (var e in entries)
                {
                    var memberKey = e.AudioObjectIDOrAudioObject?.BasicAudioObjectID;
                    var sound = memberKey is not null && store.Holds(Kinds.BasicAudioObject, memberKey) ? Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, memberKey)!) : null;
                    told.Add(new ListedMember
                    {
                        ObjectID = memberKey ?? "", PoV = e.AudioObjectSpaceTime?.SpatialAttitude1,
                        Time = e.AudioObjectSpaceTime?.Time is { SimpleTimeData.Count: > 0 } time ? time : null,
                        Level = sound?.BasicAudioObjectProperties?.Level
                    });
                    var end = SoundLength.EndOf(e.AudioObjectSpaceTime) ?? SoundLength.StartOf(e.AudioObjectSpaceTime) + (SoundLength.Seconds(sound?.Data) ?? 0);
                    duration = Math.Max(duration ?? 0, end);
                }
                if (duration is { } d) duration = Math.Round(d, 6);
            }
            found.Add(new ListedObject
            {
                ObjectID = name, Kind = ListKinds.AudioScene, MemberObjectIDs = members,
                ObjectCount = named is null ? null : entries?.Count, Duration = duration,
                Members = told
            });
        }
        return found;
    }

    // DELETION of a Scene by its name, with all its versions. Nothing holds a Scene.
    public (DeletionOutcome Outcome, string? Reason) Delete(string? name)
    {
        if (KeptScene(name) is not { } id) return (DeletionOutcome.NotKept, null);
        store.Delete(Kinds.AudioScene, id);
        return (DeletionOutcome.Deleted, null);
    }
}
