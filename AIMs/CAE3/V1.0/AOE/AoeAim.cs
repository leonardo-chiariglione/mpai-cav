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

namespace Mpai.Cae.Aoe;

// ---------------------------------------------------------------------------
//  CAE-AOE-V1.0 - Audio Object Editing.
//
//  WHERE THE OBJECTS ARE (the author, 2026/11/08: "always write data type and port number; when Shared Storage also write the Module name"). Every instance is kept in the
//  Private Storage of the Module at   <Data Type>#<Port>/<the Object's own ID>@<version>   - OSD-AUO-V1.5#1/band@2, OSD-BAO-V1.5#1/violin@1 - by ModuleStore; the Audio Object
//  the Module gives (its Output Port) is also offered in its Shared Storage, at <Module>/OSD-AUO-V1.5#1/<ID>. There is no table and no counter: what is kept, the version in
//  force (the highest) and the version number are found in what is stored.
//
//  AN OBJECT IS KNOWN BY ITS NAME (the author, 2026/10/26). The Object as stored, as output and as a User Command names it carries the name the User gave it in its
//  identifier (BasicAudioObjectID, AudioObjectID): "violin" - and that ID is the key. A name is unique among the Audio Objects the Module keeps: a second Object given a name
//  that is taken is "violin 2". An Object with no name is not created.
//
//  Editing Principle preserved: every edit produces a NEW version via a fresh Put - never an overwrite of an existing version's content. The ID then designates the new
//  version; the earlier one keeps its place, for undo. A BasicAudioObject is the degenerate case of an AudioObject with no sub-objects, not a separate type to special-case.
// ---------------------------------------------------------------------------
public sealed class AoeAim
{
    private readonly ModuleStore store;

    // The M-Instance or U-Environment the Objects are made in: OSD requires the
    // first on an Audio Object. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    // The Acoustic Profiles the Module keeps, by their AcousticProfileID (M3282 UCM-4).
    public KeptProfiles Profiles { get; }

    public AoeAim(ModuleStore store, string mInstanceId = "ASM")
    {
        this.store = store;
        this.mInstanceId = mInstanceId;
        Profiles = new KeptProfiles(store);
        store.Changed += Offer;
    }

    private static readonly JsonSerializerOptions DataOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DataOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, DataOptions)!;

    private static readonly StoredKind[] Both = [Kinds.AudioObject, Kinds.BasicAudioObject];

    // ID-stub entries: only the identifying field set, nothing else - a minimal but genuinely valid instance of the "object" branch of the schema's "object or id-string"
    // oneOf. The stub holds the ID of the Object it composes; Materialize() resolves it into the content (the version in force).
    private static BasicAudioObject IdStubBao(string id) => new() { BasicAudioObjectID = id };
    private static AudioObject IdStubAuo(string id) => new() { AudioObjectID = id };

    // A Space/Time at the origin, for a composed Object that has not been placed. Position carries a CartPosition array, not X/Y/Z properties.
    private static SpaceTime AtTheOrigin() => new()
    {
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position
            {
                PositionID   = Guid.NewGuid().ToString(),
                CartPosition = new double[] { 0, 0, 0 }
            },

            // The composed Object's own frame, completely specified: at the origin and unrotated. Roll, pitch, yaw - the aerospace order.
            Orientation = new Orientation
            {
                OrientationID = Guid.NewGuid().ToString(),
                EulerAngles   = new double[] { 0, 0, 0 }
            }
        }
    };

    // ---- what the Module offers ----

    // The Audio Object the Module gives, in its Shared Storage: the version in force, with the Objects it is made of in it (whole). One that is gone is withdrawn.
    private void Offer(StoredKind kind, string id)
    {
        if (kind != Kinds.AudioObject && kind != Kinds.BasicAudioObject) return;
        byte[]? resolved = null;
        if (Holds(id)) { try { resolved = Serialize(Materialize(id)); } catch (InvalidOperationException) { return; } }
        store.Offer(Kinds.AudioObject, id, resolved);
    }

    // What a User Command wrote, to be undone (the last first); the AIM asks for it when the command is done.
    public void ForgetSteps() => store.ForgetSteps();
    public (Func<bool> Unchanged, Action Undo)? TakeSteps() => store.TakeSteps();

    // ---- names ----

    // The name of the Audio Object (Basic or Full) the Module keeps under it; null when it keeps none. The name is the ID, and the ID is the key.
    public string? Kept(string? name) => NameBook.Clean(name) is { } clean && Holds(clean) ? clean : null;
    public bool Holds(string? name) => NameBook.Clean(name) is { } clean && (store.Holds(Kinds.BasicAudioObject, clean) || store.Holds(Kinds.AudioObject, clean));
    public bool IsBasic(string? id) => store.Holds(Kinds.BasicAudioObject, id);
    public bool IsFull(string? id) => store.Holds(Kinds.AudioObject, id);

    // Is this arriving Basic Audio Object the sound kept under its name? One that carries no sound, or the same sound, is.
    public bool IsTheSound(string id, BasicAudioObject arriving)
    {
        if (arriving.BasicAudioObjectData.Count == 0) return true;
        return IsBasic(id) && Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, id)!).Data.AsSpan().SequenceEqual(arriving.Data);
    }

    // ---- creating ---------------------------------------------------------

    // AN AUDIO OBJECT THE USER AGENT READ FROM OUTSIDE (its external storage): its sounds are stored one by one, as new Objects here - under the names they came with, the
    // next free name if a name is taken by another sound - and the Audio Object is composed of them again, its own place and time folded into theirs. A sound the Module
    // keeps already, under its name, is that one.
    public RepositoryAsset StoreAudioObject(AudioObject auo, string? name = null)
    {
        name ??= auo.AudioObjectID;
        if (NameBook.Clean(name) is null) throw new NameNeededException("the Audio Object");
        var placed = new List<(string ChildId, SpaceTime? Placement)>();
        foreach (var b in auo.BasicAudioObjects ?? [])
        {
            if (b.BAObjectIDOrBAObject is not { } bao) throw new InvalidOperationException("The Audio Object names a sound it does not hold.");
            if (NameBook.Clean(bao.BasicAudioObjectID) is not { } soundName) throw new NameNeededException("a Basic Audio Object");
            var held = IsBasic(soundName) ? soundName : null;
            if (held is null && (bao.BasicAudioObjectData.Count == 0 || bao.Data.Length == 0)) throw new InvalidOperationException("The Audio Object names a sound it does not hold.");
            var key = held is not null && IsTheSound(held, bao) ? held : CreateObject(bao, soundName).AssetId;
            placed.Add((key, Within(auo.AudioObjectSpaceTime, b.BasicAudioObjectSpaceTime)));
        }
        foreach (var s in auo.SubAudioObjects ?? [])
        {
            if (s.SubAObjectIDOrSubAObject is not { } sub) throw new InvalidOperationException("The Audio Object names an Audio Object it does not hold.");
            placed.Add((StoreAudioObject(sub).AssetId, Within(auo.AudioObjectSpaceTime, s.SubAudioObjectSpaceTime)));
        }
        if (placed.Count == 0) throw new InvalidOperationException("The Audio Object holds no sound.");
        return Compose(placed, auo.UserPoV, name);
    }

    // Where an inner placement stands in the frame of an outer one (position and yaw added, the time of the outer added to that of the inner): public for Audio Scene Editing, which places the parts of a Full Audio Object.
    public static SpaceTime Within(SpaceTime? outer, SpaceTime? inner) => Placements.Within(outer, inner);

    // Stores a BasicAudioObject under the name wanted - or the next free one - and stops there. AN OBJECT HOLDING ONE OBJECT IS BASIC; one holding more than one is full.
    // The kind is a fact about the CONTENT, not a decision taken at creation, so acquiring a file yields a BAO and nothing else.
    public RepositoryAsset CreateObject(BasicAudioObject bao, string? name = null)
    {
        if (NameBook.Clean(name ?? bao.BasicAudioObjectID) is null) throw new NameNeededException("the Basic Audio Object");
        var given = NameBook.Free(store, name ?? bao.BasicAudioObjectID, Both);
        store.Write(Kinds.BasicAudioObject, given, Serialize(bao.WithId(given)));
        return new RepositoryAsset { AssetId = given, AssetType = AssetType.BAO };
    }

    // Composes SEVERAL Objects into ONE new Audio Object, under the name given (never one made up).
    //
    // NO CONTAINER, and no first among them. What the placed Objects are placed IN is the new Object's own spatial attitude: the origin, unrotated. That is the frame,
    // and nothing among them has to serve as it. Composing ONE Object is legitimate and produces an Audio Object holding it: a new identity for a new thing, which is how
    // something is cloned to a different place.
    public RepositoryAsset Compose(
        IReadOnlyList<(string ChildId, SpaceTime? Placement)> placed,
        PointOfView? listenerPointOfView = null,
        string? name = null)
    {
        if (placed.Count == 0)
            throw new InvalidOperationException("Nothing was placed.");
        if (NameBook.Clean(name) is null) throw new NameNeededException("the Audio Object");

        var basicEntries = new List<BasicAudioObjectEntry>();
        var subEntries   = new List<SubAudioObjectEntry>();

        foreach (var (childId, _) in placed)
        {
            if (!Holds(childId))
                throw new InvalidOperationException("An Audio Object is made of Objects the Module keeps.");
        }

        var given = NameBook.Free(store, name, Both);

        foreach (var (childId, placement) in placed)
        {
            if (IsBasic(childId))
            {
                basicEntries.Add(new BasicAudioObjectEntry
                {
                    BasicAudioObjectSpaceTime = placement,
                    BAObjectIDOrBAObject      = IdStubBao(childId)
                });
            }
            else
            {
                subEntries.Add(new SubAudioObjectEntry
                {
                    SubAudioObjectSpaceTime  = placement,
                    SubAObjectIDOrSubAObject = IdStubAuo(childId)
                });
            }
        }

        store.Write(Kinds.AudioObject, given, Serialize(new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = given,

            // The frame the placed Objects sit in.
            AudioObjectSpaceTime = AtTheOrigin(),

            UserPoV = listenerPointOfView,
            BasicAudioObjectCount = basicEntries.Count,
            BasicAudioObjects = basicEntries.Count > 0 ? basicEntries : null,
            SubAudioObjectCount = subEntries.Count,
            SubAudioObjects = subEntries.Count > 0 ? subEntries : null
        }));

        return new RepositoryAsset { AssetId = given, AssetType = AssetType.AUO };
    }

    // Produces a new version of a BasicAudioObject with updated properties (Level, PerceptStatus, AcousticProfile, BasicAudioObjectIdentifier). Unspecified (null)
    // parameters keep their existing value - this edits, it does not replace. The audio DATA itself is never touched here, consistent with "BAOs are static" - only the
    // descriptive properties that sit alongside the data change, and that change is itself a new, immutable version, same as everywhere else. The new version has the name of
    // the one it comes from, and the name designates it from now on.
    //
    // listenerPointOfView is where the listener stands relative to this Object. A lone Basic Object is AT THE ORIGIN - it is the thing being auditioned; what the user may
    // move is the EAR, which is why the schema puts UserPoV on the Basic Object itself.
    public RepositoryAsset EditBasicObjectProperties(
        string id,
        double? level = null,
        bool? perceptStatus = null,
        AcousticProfile? acousticProfile = null,
        InstanceIdentifier? identifier = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!IsBasic(id))
            throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");

        var existingData = Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, id)!);
        var existingProps = existingData.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();

        var updatedProps = new BasicAudioObjectProperties
        {
            BasicAudioObjectSpaceTime = existingProps.BasicAudioObjectSpaceTime,
            Level = level ?? existingProps.Level,
            PerceptStatus = perceptStatus ?? existingProps.PerceptStatus,
            AcousticProfile = acousticProfile ?? existingProps.AcousticProfile,
            BasicAudioObjectIdentifier = identifier ?? existingProps.BasicAudioObjectIdentifier
        };

        store.Write(Kinds.BasicAudioObject, id, Serialize(new BasicAudioObject
        {
            Header = existingData.Header,
            MInstanceID = existingData.MInstanceID,
            UEnvironmentID = existingData.UEnvironmentID,
            BasicAudioObjectID = existingData.BasicAudioObjectID,
            BasicAudioObjectTime = existingData.BasicAudioObjectTime,
            ParentObjects = existingData.ParentObjects,
            ChildObjects = existingData.ChildObjects,
            BasicAudioObjectData = existingData.BasicAudioObjectData,
            UserPoV = listenerPointOfView ?? existingData.UserPoV,
            DeviceID = existingData.DeviceID,
            BasicAudioObjectProperties = updatedProps,
            AudioQualifier = existingData.AudioQualifier,
            DataXMData = existingData.DataXMData,
            DescrMetadata = existingData.DescrMetadata
        }));

        return new RepositoryAsset { AssetId = id, AssetType = AssetType.BAO };
    }

    // THE PROPERTIES OF AN OBJECT, whether Basic or Full (the author, 2026/11/08: everything in ASM is Full, a Basic Object is the case of one part). A Basic Audio Object is edited as
    // EditBasicObjectProperties does. A Full Audio Object has its Level and its Acoustic Profile on EACH Basic Audio Object it holds, at any depth (the simplest reading: the loudness or the
    // room of an Object is that of every sound in it), and the point of view the listener stands at on the Audio Object itself. Each Basic Audio Object, and each Audio Object above it, is a
    // new version under its name; the name of the Object.
    public RepositoryAsset EditObjectProperties(string id, double? level = null, AcousticProfile? acousticProfile = null, PointOfView? listenerPointOfView = null)
    {
        if (IsBasic(id)) return EditBasicObjectProperties(id, level: level, acousticProfile: acousticProfile, listenerPointOfView: listenerPointOfView);
        if (!IsFull(id)) throw new InvalidOperationException("The Module keeps no such Object.");
        var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, id)!);
        var basics = new List<BasicAudioObjectEntry>();
        var subs = new List<SubAudioObjectEntry>();
        foreach (var e in auo.BasicAudioObjects ?? [])
        {
            var child = e.BAObjectIDOrBAObject?.BasicAudioObjectID ?? throw new InvalidOperationException("The Audio Object names a sound it does not hold.");
            if (level is not null || acousticProfile is not null) EditBasicObjectProperties(child, level: level, acousticProfile: acousticProfile);
            basics.Add(new BasicAudioObjectEntry { BasicAudioObjectSpaceTime = e.BasicAudioObjectSpaceTime, BAObjectIDOrBAObject = IdStubBao(child) });
        }
        foreach (var e in auo.SubAudioObjects ?? [])
        {
            var child = e.SubAObjectIDOrSubAObject?.AudioObjectID ?? throw new InvalidOperationException("The Audio Object names an Audio Object it does not hold.");
            if (level is not null || acousticProfile is not null) EditObjectProperties(child, level, acousticProfile);
            subs.Add(new SubAudioObjectEntry { SubAudioObjectSpaceTime = e.SubAudioObjectSpaceTime, SubAObjectIDOrSubAObject = IdStubAuo(child) });
        }
        store.Write(Kinds.AudioObject, id, Serialize(new AudioObject
        {
            Header = auo.Header, MInstanceID = auo.MInstanceID, UEnvironmentID = auo.UEnvironmentID, AudioObjectID = id,
            AudioObjectTime = auo.AudioObjectTime, AudioObjectSpaceTime = auo.AudioObjectSpaceTime, UserPoV = listenerPointOfView ?? auo.UserPoV,
            ParentAudioObjectIDs = auo.ParentAudioObjectIDs,
            BasicAudioObjectCount = basics.Count, BasicAudioObjects = basics.Count > 0 ? basics : null,
            SubAudioObjectCount = subs.Count, SubAudioObjects = subs.Count > 0 ? subs : null,
            DataXMData = auo.DataXMData, DescrMetadata = auo.DescrMetadata
        }));
        return new RepositoryAsset { AssetId = id, AssetType = AssetType.AUO };
    }

    // THE FEATURES OF AN OBJECT, EDITED (the author, 2026/10/26): a defined list, each changed by a value; every edit is a new version, which has the name of the Object
    // (or the new name, when the name is a feature changed). The sound itself is not a feature. The Object is named by its name; the name it has after, or why none.
    //   Audio Object:        Name, Time, SpaceTime, UserPoV, ParentAudioObjectIDs, DescrMetadata, Children (the SpaceTime of a child, by its name, inside the parent)
    //   Basic Audio Object:  Name, Time, UserPoV, DeviceID, Level, PerceptStatus, AcousticProfile, AudioQualifier, DescrMetadata
    public (string? Key, string? Problem) ChangeFeatures(string id, ObjectChange change)
    {
        var basic = IsBasic(id);
        if (!basic && !IsFull(id)) return (null, "the Module keeps no such Object");
        var wrong = new List<string>();
        if (basic)
        {
            if (change.SpaceTime is not null) wrong.Add("SpaceTime");
            if (change.ParentAudioObjectIDs is not null) wrong.Add("ParentAudioObjectIDs");
            if (change.Children is { Count: > 0 }) wrong.Add("Children");
        }
        else
        {
            if (change.DeviceID is not null) wrong.Add("DeviceID");
            if (change.Level is not null) wrong.Add("Level");
            if (change.PerceptStatus is not null) wrong.Add("PerceptStatus");
            if (change.AcousticProfile is not null) wrong.Add("AcousticProfile");
            if (change.AudioQualifier is not null) wrong.Add("AudioQualifier");
        }
        if (wrong.Count > 0)
            return (null, $"{string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not {(basic ? "a feature of a Basic Audio Object (it is of an Audio Object)" : "a feature of an Audio Object (it is of a Basic Audio Object)")}");
        if (change.Name is not null && NameBook.Clean(change.Name) is null) return (null, "Name: the new name is empty");
        if (change.Level is { } level && (double.IsNaN(level) || double.IsInfinity(level))) return (null, "Level: a number (LUFS) is required");
        // The Acoustic Profile of the Object, in full (and then kept under its identifier) or the identifier of a kept one.
        var profile = change.AcousticProfile;
        if (profile is { Reference: true })
        {
            profile = Profiles.Get(profile.AcousticProfileID);
            if (profile is null) return (null, $"the Module keeps no Acoustic Profile named \"{change.AcousticProfile!.AcousticProfileID}\"");
        }
        else if (profile is not null) Profiles.Keep(profile);

        var name = id;
        var newName = name;
        if (NameBook.Clean(change.Name) is { } wanted && wanted != name) newName = NameBook.Free(store, wanted, Both);

        AudioObject? auo = null;
        BasicAudioObject? bao = null;
        if (basic) bao = Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, id)!);
        else
        {
            auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, id)!);
            foreach (var c in change.Children ?? [])
            {
                var child = NameBook.Clean(c.ObjectID);
                if (child is null || c.SpaceTime is null) return (null, "Children: each child is named, with its SpaceTime inside the parent");
                if (c.Unknown is { Count: > 0 }) return (null, $"Children: {string.Join(", ", c.Unknown.Keys)} is not a feature of a child");
                var isChild = (auo.BasicAudioObjects ?? []).Any(e => e.BAObjectIDOrBAObject?.BasicAudioObjectID == child)
                           || (auo.SubAudioObjects ?? []).Any(e => e.SubAObjectIDOrSubAObject?.AudioObjectID == child);
                if (!isChild) return (null, $"Children: the Audio Object has no child named {child}");
            }
        }

        byte[] content;
        if (basic)
        {
            var props = bao!.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();
            content = Serialize(new BasicAudioObject
            {
                Header = bao.Header, MInstanceID = bao.MInstanceID, UEnvironmentID = bao.UEnvironmentID,
                BasicAudioObjectID = newName,
                BasicAudioObjectTime = change.Time is { } t ? new SpaceTime { MInstanceID = bao.MInstanceID, SpaceTimeID = Guid.NewGuid().ToString(), Time = t } : bao.BasicAudioObjectTime,
                ParentObjects = bao.ParentObjects, ChildObjects = bao.ChildObjects,
                BasicAudioObjectData = bao.BasicAudioObjectData,
                UserPoV = change.UserPoV ?? bao.UserPoV,
                DeviceID = change.DeviceID ?? bao.DeviceID,
                BasicAudioObjectProperties = new BasicAudioObjectProperties
                {
                    BasicAudioObjectSpaceTime = props.BasicAudioObjectSpaceTime,
                    Level = change.Level ?? props.Level,
                    PerceptStatus = change.PerceptStatus ?? props.PerceptStatus,
                    AcousticProfile = profile ?? props.AcousticProfile,
                    BasicAudioObjectIdentifier = props.BasicAudioObjectIdentifier
                },
                AudioQualifier = change.AudioQualifier ?? bao.AudioQualifier,
                DataXMData = bao.DataXMData,
                DescrMetadata = change.DescrMetadata ?? bao.DescrMetadata
            });
        }
        else
        {
            SpaceTime? Inside(string? childName, SpaceTime? current) =>
                change.Children?.FirstOrDefault(c => NameBook.Clean(c.ObjectID) == childName)?.SpaceTime ?? current;
            var basics = auo!.BasicAudioObjects?.Select(e => new BasicAudioObjectEntry
            {
                BasicAudioObjectSpaceTime = Inside(e.BAObjectIDOrBAObject?.BasicAudioObjectID, e.BasicAudioObjectSpaceTime), BAObjectIDOrBAObject = e.BAObjectIDOrBAObject
            }).ToList();
            var subs = auo.SubAudioObjects?.Select(e => new SubAudioObjectEntry
            {
                SubAudioObjectSpaceTime = Inside(e.SubAObjectIDOrSubAObject?.AudioObjectID, e.SubAudioObjectSpaceTime), SubAObjectIDOrSubAObject = e.SubAObjectIDOrSubAObject
            }).ToList();
            content = Serialize(new AudioObject
            {
                Header = auo.Header, MInstanceID = auo.MInstanceID, UEnvironmentID = auo.UEnvironmentID,
                AudioObjectID = newName,
                AudioObjectTime = change.Time ?? auo.AudioObjectTime,
                AudioObjectSpaceTime = change.SpaceTime ?? auo.AudioObjectSpaceTime,
                UserPoV = change.UserPoV ?? auo.UserPoV,
                ParentAudioObjectIDs = change.ParentAudioObjectIDs ?? auo.ParentAudioObjectIDs,
                BasicAudioObjectCount = auo.BasicAudioObjectCount, BasicAudioObjects = basics,
                SubAudioObjectCount = auo.SubAudioObjectCount, SubAudioObjects = subs,
                DataXMData = auo.DataXMData,
                DescrMetadata = change.DescrMetadata ?? auo.DescrMetadata
            });
        }

        var kind = basic ? Kinds.BasicAudioObject : Kinds.AudioObject;
        if (newName == name) store.Write(kind, name, content);
        else
        {
            // A new name: the Object, with the versions it had, is now under the new name; the old name designates nothing. The Audio Objects of the Module that hold it hold it by the new name.
            store.Move(kind, name, newName, content);
            RenameInParents(name, newName);
        }
        return (newName, null);
    }

    // The Audio Objects that hold an Object by its name hold it by the new name, as new versions of theirs.
    private void RenameInParents(string from, string to)
    {
        foreach (var (parent, _) in store.All(Kinds.AudioObject))
        {
            var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, parent)!);
            var holds = (auo.BasicAudioObjects ?? []).Any(e => e.BAObjectIDOrBAObject?.BasicAudioObjectID == from)
                     || (auo.SubAudioObjects ?? []).Any(e => e.SubAObjectIDOrSubAObject?.AudioObjectID == from);
            if (!holds || parent == to) continue;
            var basics = auo.BasicAudioObjects?.Select(e => e.BAObjectIDOrBAObject?.BasicAudioObjectID == from
                ? new BasicAudioObjectEntry { BasicAudioObjectSpaceTime = e.BasicAudioObjectSpaceTime, BAObjectIDOrBAObject = IdStubBao(to) } : e).ToList();
            var subs = auo.SubAudioObjects?.Select(e => e.SubAObjectIDOrSubAObject?.AudioObjectID == from
                ? new SubAudioObjectEntry { SubAudioObjectSpaceTime = e.SubAudioObjectSpaceTime, SubAObjectIDOrSubAObject = IdStubAuo(to) } : e).ToList();
            store.Write(Kinds.AudioObject, parent, Serialize(new AudioObject
            {
                Header = auo.Header, MInstanceID = auo.MInstanceID, UEnvironmentID = auo.UEnvironmentID, AudioObjectID = auo.AudioObjectID,
                AudioObjectTime = auo.AudioObjectTime, AudioObjectSpaceTime = auo.AudioObjectSpaceTime, UserPoV = auo.UserPoV,
                ParentAudioObjectIDs = auo.ParentAudioObjectIDs,
                BasicAudioObjectCount = auo.BasicAudioObjectCount, BasicAudioObjects = basics,
                SubAudioObjectCount = auo.SubAudioObjectCount, SubAudioObjects = subs,
                DataXMData = auo.DataXMData, DescrMetadata = auo.DescrMetadata
            }));
        }
    }

    // ---- reading ------------------------------------------------------------

    // The Object as the Module keeps it (JSON), the version in force.
    public byte[] Raw(string id) => store.Get(IsBasic(id) ? Kinds.BasicAudioObject : Kinds.AudioObject, id) ?? throw new InvalidOperationException("The Module keeps no such Object.");

    // A stored Basic Audio Object, as it is.
    public BasicAudioObject Get(string id) =>
        IsBasic(id) ? Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, id)!) : throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");

    // Resolves a stored Object into a fully self-contained structure - its own fields plus every BAO/sub-Audio Object it (transitively) composes, with each ID-stub entry
    // replaced by its full, live content - and names it by its name. A BASIC Object materialises as itself: an Object of one, resolved so that a consumer needs no
    // separate case for it.
    public AudioObject Materialize(string id)
    {
        if (IsBasic(id))
        {
            var basic = Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, id)!);

            return new AudioObject
            {
                MInstanceID = mInstanceId,
                AudioObjectID = id,
                AudioObjectSpaceTime = new SpaceTime(),      // the schema requires one: an Object of one stands where its Object does, at the origin of its own frame
                BasicAudioObjectCount = 1,
                BasicAudioObjects = new List<BasicAudioObjectEntry>
                {
                    new BasicAudioObjectEntry { BAObjectIDOrBAObject = basic }
                }
            };
        }

        if (!IsFull(id))
            throw new InvalidOperationException("The Module keeps no such Object.");

        var audioObject = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, id)!);

        var basicChildren = new List<BasicAudioObjectEntry>();
        var subChildren = new List<SubAudioObjectEntry>();

        foreach (var entry in audioObject.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
        {
            var child = entry.BAObjectIDOrBAObject?.BasicAudioObjectID;
            if (child is null || !IsBasic(child)) continue;

            basicChildren.Add(new BasicAudioObjectEntry { BasicAudioObjectSpaceTime = entry.BasicAudioObjectSpaceTime, BAObjectIDOrBAObject = Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, child)!) });
        }

        foreach (var entry in audioObject.SubAudioObjects ?? new List<SubAudioObjectEntry>())
        {
            var child = entry.SubAObjectIDOrSubAObject?.AudioObjectID;
            if (string.IsNullOrEmpty(child) || !IsFull(child)) continue;

            subChildren.Add(new SubAudioObjectEntry { SubAudioObjectSpaceTime = entry.SubAudioObjectSpaceTime, SubAObjectIDOrSubAObject = Materialize(child) });
        }

        return new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = id,
            AudioObjectTime = audioObject.AudioObjectTime,
            AudioObjectSpaceTime = audioObject.AudioObjectSpaceTime,
            ParentAudioObjectIDs = audioObject.ParentAudioObjectIDs,
            UserPoV = audioObject.UserPoV,
            DescrMetadata = audioObject.DescrMetadata,
            BasicAudioObjectCount = basicChildren.Count,
            BasicAudioObjects = basicChildren.Count > 0 ? basicChildren : null,
            SubAudioObjectCount = subChildren.Count,
            SubAudioObjects = subChildren.Count > 0 ? subChildren : null
        };
    }

    // THE LIST (the author, 2026/10/26): the Audio Objects and Basic Audio Objects the Module keeps - the version in force of each - by name; an Audio Object with the names
    // of the Objects it is made of.
    public IReadOnlyList<ListedObject> Listing(string requested, string? named = null)
    {
        var found = new List<ListedObject>();
        var all = store.All(Kinds.BasicAudioObject).Select(e => (e.Id, e.Version, Basic: true))
            .Concat(store.All(Kinds.AudioObject).Select(e => (e.Id, e.Version, Basic: false)))
            .OrderBy(e => e.Id, StringComparer.Ordinal);
        foreach (var (name, version, basic) in all)
        {
            if (named is not null && name != named) continue;
            if (!ListKinds.Asks(requested, basic ? ListKinds.BasicAudioObject : ListKinds.AudioObject)) continue;
            List<string>? members = null;
            if (!basic)
            {
                var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, name)!);
                members = (auo.BasicAudioObjects ?? []).Select(e => e.BAObjectIDOrBAObject?.BasicAudioObjectID)
                    .Concat((auo.SubAudioObjects ?? []).Select(e => e.SubAObjectIDOrSubAObject?.AudioObjectID))
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            }
            found.Add(Describe(name, version, basic, members));
        }
        return found;
    }

    // WHAT THE LIST SAYS OF AN OBJECT (the author, 2026/10/26): its Kind, its Duration (seconds: that of its sound, or for an Audio Object the end of the child that lasts longest
    // inside it), the number of children it is made of, its Version.
    private ListedObject Describe(string name, int version, bool basic, List<string>? members)
    {
        double? duration = null;
        var children = 0;
        if (basic) duration = SoundLength.Seconds(Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, name)!).Data);
        else
        {
            var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, name)!);
            children = (auo.BasicAudioObjects?.Count ?? 0) + (auo.SubAudioObjects?.Count ?? 0);
            duration = DurationOf(name);
        }
        return new ListedObject
        {
            ObjectID = name, Kind = basic ? ListKinds.BasicAudioObject : ListKinds.AudioObject, MemberObjectIDs = members,
            Duration = duration, ChildCount = children, Version = version
        };
    }

    private double? DurationOf(string id)
    {
        var auo = Deserialize<AudioObject>(store.Get(Kinds.AudioObject, id)!);
        var ends = new List<double>();
        foreach (var e in auo.BasicAudioObjects ?? [])
            if (e.BAObjectIDOrBAObject?.BasicAudioObjectID is { } k && IsBasic(k) &&
                SoundLength.Seconds(Deserialize<BasicAudioObject>(store.Get(Kinds.BasicAudioObject, k)!).Data) is { } d) ends.Add(SoundLength.StartOf(e.BasicAudioObjectSpaceTime) + d);
        foreach (var e in auo.SubAudioObjects ?? [])
            if (e.SubAObjectIDOrSubAObject?.AudioObjectID is { } k && IsFull(k) && DurationOf(k) is { } d) ends.Add(SoundLength.StartOf(e.SubAudioObjectSpaceTime) + d);
        return ends.Count > 0 ? Math.Round(ends.Max(), 6) : null;
    }

    // ---- Deletion ----

    // DELETION: an Object by its name, with all its versions. Not deleted if a Scene holds it - an Object that is a member of a Scene (the Scene's version in force names it):
    // the Scene is deleted first. Found by reading the Scenes that are kept, not from a table.
    public (DeletionOutcome Outcome, string? Reason) Delete(string? name)
    {
        if (Kept(name) is not { } id) return (DeletionOutcome.NotKept, null);
        var holders = new List<string>();
        foreach (var (scene, _) in store.All(Kinds.AudioScene))
        {
            var bytes = store.Get(Kinds.AudioScene, scene);
            if (bytes is null) continue;
            var entries = Deserialize<BasicAudioSceneDescriptors>(bytes).BasicAudioSceneDescriptorsEntries;
            if (entries?.Any(e => e.AudioObjectIDOrAudioObject?.BasicAudioObjectID == id) == true) holders.Add(scene);
        }
        if (holders.Count > 0) return (DeletionOutcome.Held, $"it is a member of {string.Join(", ", holders)}: delete that first");
        store.Delete(IsBasic(id) ? Kinds.BasicAudioObject : Kinds.AudioObject, id);
        return (DeletionOutcome.Deleted, null);
    }
}
