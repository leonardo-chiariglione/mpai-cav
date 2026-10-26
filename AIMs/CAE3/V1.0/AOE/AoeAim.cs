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
//  Ported directly onto the proposed MPAI-AIF Shared Storage API
//  (Put/Get/Delete/List/Exists/GetKeyInfo) - no intermediate Repository
//  class or method vocabulary. The Module keeps each Object under a typed
//  storage KEY (BAO000001, AUO000002 ...): the type prefix makes it
//  self-describing for List(prefix) queries (Section 4.1 of the Shared
//  Storage proposal). That key is PRIVATE to the Module.
//
//  AN OBJECT IS KNOWN BY ITS NAME (the author, 2026/10/26). The Object as stored,
//  as output and as a User Command names it carries the name the User gave it in
//  its identifier (BasicAudioObjectID, AudioObjectID): "violin". The NameBook of the
//  Module is the table between the name and the key of the version in force; the
//  key never leaves the Module. A name is unique among the Audio Objects the Module
//  keeps: a second Object given a name that is taken is "violin 2". An Object with
//  no name is not created.
//
//  Editing Principle preserved exactly: every edit produces a NEW key via a
//  fresh Put - never an overwrite of an existing key's content. The name then
//  designates the new version; the earlier one keeps its key and its name, for undo.
//  A BasicAudioObject is the degenerate case of an AudioObject with no
//  sub-objects, not a separate type to special-case.
// ---------------------------------------------------------------------------
public sealed class AoeAim
{
    private readonly ISharedStorage storage;

    // The M-Instance or U-Environment the Objects are made in: OSD requires the
    // first on an Audio Object. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    public NameBook Names { get; }

    public AoeAim(ISharedStorage storage, string mInstanceId = "ASM")
    {
        this.storage = storage;
        this.mInstanceId = mInstanceId;
        Names = new NameBook(storage);
    }

    private static readonly JsonSerializerOptions DataOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DataOptions);
    private static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, DataOptions)!;

    // A fresh, sequential, type-prefixed key per version (AUO000001, AUO000002, ...). The counter is itself a reserved key
    // ("_counter:" prefix, which List(typePrefix) never matches), so it persists across sessions exactly like everything else.
    private string NextId(string typePrefix)
    {
        var counterKey = $"_counter:{typePrefix}";
        long next = storage.Exists(counterKey)
            ? long.Parse(Encoding.UTF8.GetString(storage.Get(counterKey))) + 1
            : 1;
        storage.Put(counterKey, Encoding.UTF8.GetBytes(next.ToString()));
        return $"{typePrefix}{next:D6}";
    }

    // Per Section 4.3 of the Shared Storage proposal.
    private void PutReference(string fromId, string toId)
    {
        storage.Put($"ref:{fromId}:{toId}", Array.Empty<byte>());
        storage.Put($"refby:{toId}:{fromId}", Array.Empty<byte>());
    }

    private static bool IsType(string assetId, string prefix) => assetId.StartsWith(prefix, StringComparison.Ordinal);

    // ID-stub entries: only the identifying field set, nothing else - a minimal but genuinely valid instance of the "object" branch of the schema's "object or id-string"
    // oneOf. The stub holds the KEY of the version it composes (a composition composes the versions it was made of); Materialize() resolves it into the content, which
    // carries the name.
    private static BasicAudioObject IdStubBao(string key) => new() { BasicAudioObjectID = key };
    private static AudioObject IdStubAuo(string key) => new() { AudioObjectID = key };

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

    // ---- names and keys ---------------------------------------------------

    // The key of the version in force under a name; null when the Module keeps no Audio Object of that name.
    public string? KeyOf(string? name) => Names.KeyOf(NamedKind.AudioObject, name);
    public string? NameOf(string? key) => Names.NameOf(key);
    public bool Holds(string? name) => KeyOf(name) is not null;

    // The name designates this version (an earlier one put back by a revocation). Replacing is the version it takes the place of: if that one was renamed, the new name
    // designates nothing from now on.
    public void Select(string? key, string? replacing = null)
    {
        if (key is null || Names.NameOf(key) is not { } name) return;
        Names.Point(NamedKind.AudioObject, name, key);
        if (replacing is not null && Names.NameOf(replacing) is { } other && other != name && Names.KeyOf(NamedKind.AudioObject, other) == replacing)
            Names.Release(NamedKind.AudioObject, other);
    }

    // A new version of a thing: a fresh key which the thing's name designates from now on.
    private string NewVersion(string ofKey, string prefix)
    {
        var name = Names.NameOf(ofKey) ?? throw new InvalidOperationException("The Object has no name.");
        var key = NextId(prefix);
        Names.Point(NamedKind.AudioObject, name, key);
        Names.Link(key, ofKey);
        return key;
    }

    // Is this arriving Basic Audio Object the sound kept under its name? One that carries no sound, or the same sound, is.
    public bool IsTheSound(string key, BasicAudioObject arriving)
    {
        if (arriving.BasicAudioObjectData.Count == 0) return true;
        return IsType(key, "BAO") && storage.Exists(key) && Deserialize<BasicAudioObject>(storage.Get(key)).Data.AsSpan().SequenceEqual(arriving.Data);
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
            var held = KeyOf(soundName);
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

    private static SpaceTime Within(SpaceTime? outer, SpaceTime? inner)
    {
        static double[] P(SpaceTime? s) => s?.SpatialAttitude1?.Position?.CartPosition is { Length: >= 3 } c ? c : new double[3];
        static double Yaw(SpaceTime? s) => s?.SpatialAttitude1?.Orientation?.EulerAngles is { Length: >= 3 } a ? a[2] : 0;
        static double Seconds(double v, string? unit) => unit switch { "01" => v / 1e3, "10" => v / 1e6, "11" => v / 1e9, _ => v };
        static TimeSegment? Segment(SpaceTime? s) => s?.Time?.SimpleTimeData?.FirstOrDefault(x => x.TimeType == false);
        double Start(SpaceTime? s) => Segment(s) is { } g ? Seconds(g.StartTime, g.TimeUnit) : 0;
        double? End(SpaceTime? s) => Segment(s) is { } g && g.EndTime > g.StartTime ? Seconds(g.EndTime, g.TimeUnit) : null;

        var (o, i) = (P(outer), P(inner));
        var from = Start(outer);
        var begins = from + Start(inner);
        var ends = End(inner) is { } e ? from + e : (double?)null;
        return new SpaceTime
        {
            SpatialAttitude1 = new SpatialAttitude
            {
                ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
                Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = [o[0] + i[0], o[1] + i[1], o[2] + i[2]] },
                Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, Yaw(outer) + Yaw(inner)] }
            },
            Time = new SimpleTime
            {
                SimpleTimeID = Guid.NewGuid().ToString(),
                SimpleTimeData = { new TimeSegment { FlagsByte = 0, StartTime = begins, EndTime = ends is { } x && x > begins ? x : begins, AccuracyMode = "single", AccuracyPlusMinus = 0.0, TimeType = false, TimeUnit = "00" } }
            }
        };
    }

    // Stores a BasicAudioObject under the name wanted - or the next free one - and stops there. AN OBJECT HOLDING ONE OBJECT IS BASIC; one holding more than one is full.
    // The kind is a fact about the CONTENT, not a decision taken at creation, so acquiring a file yields a BAO and nothing else.
    public RepositoryAsset CreateObject(BasicAudioObject bao, string? name = null)
    {
        if (NameBook.Clean(name ?? bao.BasicAudioObjectID) is null) throw new NameNeededException("the Basic Audio Object");
        var key = NextId("BAO");
        var given = Names.Claim(NamedKind.AudioObject, name ?? bao.BasicAudioObjectID, key);
        storage.Put(key, Serialize(bao.WithId(given)));

        return new RepositoryAsset { AssetId = key, AssetType = AssetType.BAO };
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
            if (!storage.Exists(childId))
                throw new InvalidOperationException("An Audio Object is made of Objects the Module keeps.");
            if (!IsType(childId, "BAO") && !IsType(childId, "AUO"))
                throw new InvalidOperationException("An Audio Object is made of Basic Audio Objects and Audio Objects.");
        }

        var newKey = NextId("AUO");
        var given = Names.Claim(NamedKind.AudioObject, name, newKey);

        foreach (var (childId, placement) in placed)
        {
            PutReference(newKey, childId);

            if (IsType(childId, "BAO"))
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

        storage.Put(newKey, Serialize(new AudioObject
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

        return new RepositoryAsset { AssetId = newKey, AssetType = AssetType.AUO };
    }

    // Produces a new version of a BasicAudioObject with updated properties (Level, PerceptStatus, AcousticProfile, BasicAudioObjectIdentifier). Unspecified (null)
    // parameters keep their existing value - this edits, it does not replace. The audio DATA itself is never touched here, consistent with "BAOs are static" - only the
    // descriptive properties that sit alongside the data change, and that change is itself a new, immutable key, same as everywhere else. The new version has the name of
    // the one it comes from, and the name designates it from now on.
    //
    // listenerPointOfView is where the listener stands relative to this Object. A lone Basic Object is AT THE ORIGIN - it is the thing being auditioned; what the user may
    // move is the EAR, which is why the schema puts UserPoV on the Basic Object itself.
    public RepositoryAsset EditBasicObjectProperties(
        string basicAudioObjectKey,
        double? level = null,
        bool? perceptStatus = null,
        AcousticProfile? acousticProfile = null,
        InstanceIdentifier? identifier = null,
        PointOfView? listenerPointOfView = null)
    {
        if (!storage.Exists(basicAudioObjectKey) || !IsType(basicAudioObjectKey, "BAO"))
            throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");

        var existingData = Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectKey));
        var existingProps = existingData.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();

        var updatedProps = new BasicAudioObjectProperties
        {
            BasicAudioObjectSpaceTime = existingProps.BasicAudioObjectSpaceTime,
            Level = level ?? existingProps.Level,
            PerceptStatus = perceptStatus ?? existingProps.PerceptStatus,
            AcousticProfile = acousticProfile ?? existingProps.AcousticProfile,
            BasicAudioObjectIdentifier = identifier ?? existingProps.BasicAudioObjectIdentifier
        };

        var newKey = NewVersion(basicAudioObjectKey, "BAO");
        storage.Put(newKey, Serialize(new BasicAudioObject
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

        return new RepositoryAsset { AssetId = newKey, AssetType = AssetType.BAO };
    }

    // THE FEATURES OF AN OBJECT, EDITED (the author, 2026/10/26): a defined list, each changed by a value; every edit is a new version, which has the name of the Object
    // (or the new name, when the name is a feature changed). The sound itself is not a feature. The Object is named by its name; the key of the version made, or why none.
    //   Audio Object:        Name, Time, SpaceTime, UserPoV, ParentAudioObjectIDs, DescrMetadata, Children (the SpaceTime of a child, by its name, inside the parent)
    //   Basic Audio Object:  Name, Time, UserPoV, DeviceID, Level, PerceptStatus, AcousticProfile, AudioQualifier, DescrMetadata
    public (string? Key, string? Problem) ChangeFeatures(string key, ObjectChange change)
    {
        var basic = IsType(key, "BAO");
        if (!basic && !IsType(key, "AUO")) return (null, "the Module keeps no such Object");
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

        var name = Names.NameOf(key) ?? throw new InvalidOperationException("The Object has no name.");
        var newName = name;
        if (NameBook.Clean(change.Name) is { } wanted && wanted != name) newName = Names.Free(NamedKind.AudioObject, wanted);

        AudioObject? auo = null;
        BasicAudioObject? bao = null;
        if (basic) bao = Deserialize<BasicAudioObject>(storage.Get(key));
        else
        {
            auo = Deserialize<AudioObject>(storage.Get(key));
            foreach (var c in change.Children ?? [])
            {
                var child = NameBook.Clean(c.ObjectID);
                if (child is null || c.SpaceTime is null) return (null, "Children: each child is named, with its SpaceTime inside the parent");
                if (c.Unknown is { Count: > 0 }) return (null, $"Children: {string.Join(", ", c.Unknown.Keys)} is not a feature of a child");
                var isChild = (auo.BasicAudioObjects ?? []).Any(e => NameOf(e.BAObjectIDOrBAObject?.BasicAudioObjectID) == child)
                           || (auo.SubAudioObjects ?? []).Any(e => NameOf(e.SubAObjectIDOrSubAObject?.AudioObjectID) == child);
                if (!isChild) return (null, $"Children: the Audio Object has no child named {child}");
            }
        }

        var newKey = NextId(basic ? "BAO" : "AUO");
        Names.Point(NamedKind.AudioObject, newName, newKey);
        Names.Link(newKey, key);
        if (newName != name) Names.Release(NamedKind.AudioObject, name);       // the old name designates nothing; its versions keep it

        if (basic)
        {
            var props = bao!.BasicAudioObjectProperties ?? new BasicAudioObjectProperties();
            storage.Put(newKey, Serialize(new BasicAudioObject
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
                    AcousticProfile = change.AcousticProfile ?? props.AcousticProfile,
                    BasicAudioObjectIdentifier = props.BasicAudioObjectIdentifier
                },
                AudioQualifier = change.AudioQualifier ?? bao.AudioQualifier,
                DataXMData = bao.DataXMData,
                DescrMetadata = change.DescrMetadata ?? bao.DescrMetadata
            }));
        }
        else
        {
            SpaceTime? Inside(string? childName, SpaceTime? current) =>
                change.Children?.FirstOrDefault(c => NameBook.Clean(c.ObjectID) == childName)?.SpaceTime ?? current;
            var basics = auo!.BasicAudioObjects?.Select(e => new BasicAudioObjectEntry
            {
                BasicAudioObjectSpaceTime = Inside(NameOf(e.BAObjectIDOrBAObject?.BasicAudioObjectID), e.BasicAudioObjectSpaceTime), BAObjectIDOrBAObject = e.BAObjectIDOrBAObject
            }).ToList();
            var subs = auo.SubAudioObjects?.Select(e => new SubAudioObjectEntry
            {
                SubAudioObjectSpaceTime = Inside(NameOf(e.SubAObjectIDOrSubAObject?.AudioObjectID), e.SubAudioObjectSpaceTime), SubAObjectIDOrSubAObject = e.SubAObjectIDOrSubAObject
            }).ToList();
            foreach (var childKey in (basics ?? []).Select(e => e.BAObjectIDOrBAObject?.BasicAudioObjectID).Concat((subs ?? []).Select(e => e.SubAObjectIDOrSubAObject?.AudioObjectID)))
                if (!string.IsNullOrEmpty(childKey)) PutReference(newKey, childKey);
            storage.Put(newKey, Serialize(new AudioObject
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
            }));
        }
        return (newKey, null);
    }

    // ---- reading ------------------------------------------------------------

    // Does the Module hold this key? (Keys are the Module's own: a name is looked for with Holds.)
    public bool Has(string? key) => !string.IsNullOrWhiteSpace(key) && storage.Exists(key);

    // The Object as Shared Storage keeps it (JSON).
    public byte[] Raw(string key) => storage.Get(key);

    // A stored Basic Audio Object, as it is.
    public BasicAudioObject Get(string basicAudioObjectKey) =>
        IsType(basicAudioObjectKey, "BAO") && storage.Exists(basicAudioObjectKey)
            ? Deserialize<BasicAudioObject>(storage.Get(basicAudioObjectKey))
            : throw new InvalidOperationException("The Module keeps no such Basic Audio Object.");

    // Resolves a stored Object into a fully self-contained structure - its own fields plus every BAO/sub-Audio Object it (transitively) composes, with each ID-stub entry
    // replaced by its full, live content - and names it by its name. A BASIC Object materialises as itself: an Object of one, resolved so that a consumer needs no
    // separate case for it.
    public AudioObject Materialize(string key)
    {
        if (!storage.Exists(key))
            throw new InvalidOperationException("The Module keeps no such Object.");

        var name = NameOf(key) ?? throw new InvalidOperationException("The Object has no name.");

        if (IsType(key, "BAO"))
        {
            var basic = Deserialize<BasicAudioObject>(storage.Get(key));

            return new AudioObject
            {
                MInstanceID = mInstanceId,
                AudioObjectID = name,
                AudioObjectSpaceTime = new SpaceTime(),      // the schema requires one: an Object of one stands where its Object does, at the origin of its own frame
                BasicAudioObjectCount = 1,
                BasicAudioObjects = new List<BasicAudioObjectEntry>
                {
                    new BasicAudioObjectEntry { BAObjectIDOrBAObject = basic }
                }
            };
        }

        if (!IsType(key, "AUO"))
            throw new InvalidOperationException("The Module keeps no such Object.");

        var audioObject = Deserialize<AudioObject>(storage.Get(key));

        var basicChildren = new List<BasicAudioObjectEntry>();
        var subChildren = new List<SubAudioObjectEntry>();

        foreach (var entry in audioObject.BasicAudioObjects ?? new List<BasicAudioObjectEntry>())
        {
            var childKey = entry.BAObjectIDOrBAObject?.BasicAudioObjectID;
            if (childKey is null || !storage.Exists(childKey)) continue;

            basicChildren.Add(new BasicAudioObjectEntry { BasicAudioObjectSpaceTime = entry.BasicAudioObjectSpaceTime, BAObjectIDOrBAObject = Deserialize<BasicAudioObject>(storage.Get(childKey)) });
        }

        foreach (var entry in audioObject.SubAudioObjects ?? new List<SubAudioObjectEntry>())
        {
            var childKey = entry.SubAObjectIDOrSubAObject?.AudioObjectID;
            if (string.IsNullOrEmpty(childKey) || !storage.Exists(childKey) || !IsType(childKey, "AUO")) continue;

            subChildren.Add(new SubAudioObjectEntry { SubAudioObjectSpaceTime = entry.SubAudioObjectSpaceTime, SubAObjectIDOrSubAObject = Materialize(childKey) });
        }

        return new AudioObject
        {
            MInstanceID = mInstanceId,
            AudioObjectID = name,
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
    // of the Objects it is made of. Never a key.
    public IReadOnlyList<ListedObject> Listing(string requested, string? named = null)
    {
        var found = new List<ListedObject>();
        foreach (var (name, key) in Names.All(NamedKind.AudioObject))
        {
            if (named is not null && name != named) continue;
            var basic = IsType(key, "BAO");
            if (!ListKinds.Asks(requested, basic ? ListKinds.BasicAudioObject : ListKinds.AudioObject)) continue;
            List<string>? members = null;
            if (!basic && storage.Exists(key))
            {
                var auo = Deserialize<AudioObject>(storage.Get(key));
                members = (auo.BasicAudioObjects ?? []).Select(e => NameOf(e.BAObjectIDOrBAObject?.BasicAudioObjectID))
                    .Concat((auo.SubAudioObjects ?? []).Select(e => NameOf(e.SubAObjectIDOrSubAObject?.AudioObjectID)))
                    .Where(n => n is not null).Select(n => n!).ToList();
            }
            found.Add(Describe(name, key, basic, members));
        }
        return found;
    }

    // WHAT THE LIST SAYS OF AN OBJECT (the author, 2026/10/26): its Kind, its Duration (seconds: that of its sound, or for an Audio Object the end of the child that lasts longest
    // inside it), the number of children it is made of, its Version.
    private ListedObject Describe(string name, string key, bool basic, List<string>? members)
    {
        double? duration = null;
        var children = 0;
        if (storage.Exists(key))
        {
            if (basic) duration = SoundLength.Seconds(Deserialize<BasicAudioObject>(storage.Get(key)).Data);
            else
            {
                var auo = Deserialize<AudioObject>(storage.Get(key));
                children = (auo.BasicAudioObjects?.Count ?? 0) + (auo.SubAudioObjects?.Count ?? 0);
                duration = DurationOf(key);
            }
        }
        return new ListedObject
        {
            ObjectID = name, Kind = basic ? ListKinds.BasicAudioObject : ListKinds.AudioObject, MemberObjectIDs = members,
            Duration = duration, ChildCount = children, Version = Names.Depth(key)
        };
    }

    private double? DurationOf(string auoKey)
    {
        var auo = Deserialize<AudioObject>(storage.Get(auoKey));
        var ends = new List<double>();
        foreach (var e in auo.BasicAudioObjects ?? [])
            if (e.BAObjectIDOrBAObject?.BasicAudioObjectID is { } k && storage.Exists(k) && IsType(k, "BAO") &&
                SoundLength.Seconds(Deserialize<BasicAudioObject>(storage.Get(k)).Data) is { } d) ends.Add(SoundLength.StartOf(e.BasicAudioObjectSpaceTime) + d);
        foreach (var e in auo.SubAudioObjects ?? [])
            if (e.SubAObjectIDOrSubAObject?.AudioObjectID is { } k && storage.Exists(k) && IsType(k, "AUO") && DurationOf(k) is { } d) ends.Add(SoundLength.StartOf(e.SubAudioObjectSpaceTime) + d);
        return ends.Count > 0 ? Math.Round(ends.Max(), 6) : null;
    }

    // --- Deletion (surfaces the sixth Shared Storage primitive, Delete) ---
    public IReadOnlyList<string> ReferencedBy(string key) =>
        storage.List($"refby:{key}:").Select(k => k[$"refby:{key}:".Length..]).ToList();

    public IReadOnlyList<string> References(string key) =>
        storage.List($"ref:{key}:").Select(k => k[$"ref:{key}:".Length..]).ToList();

    // Deletes a single key together with its own ref:/refby: bookkeeping (both directions). Deleting a non-existent key is not an error (Section 4.10.3). Does NOT itself
    // cascade - the caller decides, one level, with user consent.
    public void Delete(string key)
    {
        foreach (var toId in References(key))
        {
            storage.Delete($"ref:{key}:{toId}");
            storage.Delete($"refby:{toId}:{key}");
        }
        foreach (var fromId in ReferencedBy(key))
        {
            storage.Delete($"ref:{fromId}:{key}");
            storage.Delete($"refby:{key}:{fromId}");
        }
        storage.Delete(key);
    }
}
