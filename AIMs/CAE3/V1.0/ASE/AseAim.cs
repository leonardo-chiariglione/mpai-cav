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
//  Ported directly onto the proposed MPAI-AIF Shared Storage API, same
//  approach as AoeAim: no intermediate Repository class or method
//  vocabulary. The Module keeps each version of a Scene under a typed storage
//  KEY (BAS000001 ...), private to the Module.
//
//  A SCENE IS KNOWN BY ITS NAME (the author, 2026/10/26), as an Object is: the Scene as
//  stored, as output and as a User Command names it carries the name the User gave it in
//  its identifier (BasicAudioSceneDescriptorsID): "concert". The Scene is made of
//  Basic Audio Objects, which it holds by the key of the version it was made with and
//  tells by name. A name is unique among the Scenes; a second Scene given a name that is
//  taken is "concert 2". A Scene with no name is not created.
//
//  The Scene the Module edits is a BasicAudioSceneDescriptors (BAS) composing
//  BasicAudioObjects directly - the genuinely simple case, not an artificially-wrapped
//  AudioObject standing in for one. It leaves the Module inside an Audio Scene of one
//  sub-scene (AseAimProcessor).
// ---------------------------------------------------------------------------
public sealed class AseAim
{
    private readonly ISharedStorage storage;

    // NO AoeAim HERE. This class used to hold one and call it to expand each child object of a scene - one AIM invoking another directly, with no Controller between them.
    // The Objects are read from the Module's Shared Storage, where Audio Object Editing keeps them.
    // The M-Instance or U-Environment the Scenes are made in: OSD requires the first
    // on a Basic Audio Scene. An application gives its own; ASM's own is "ASM".
    private readonly string mInstanceId;

    public NameBook Names { get; }

    public AseAim(ISharedStorage storage, string mInstanceId = "ASM")
    {
        this.storage = storage;
        this.mInstanceId = mInstanceId;
        Names = new NameBook(storage);
    }

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

    private string NextId(string typePrefix)
    {
        var counterKey = $"_counter:{typePrefix}";
        long next = storage.Exists(counterKey)
            ? long.Parse(Encoding.UTF8.GetString(storage.Get(counterKey))) + 1
            : 1;
        storage.Put(counterKey, Encoding.UTF8.GetBytes(next.ToString()));
        return $"{typePrefix}{next:D6}";
    }

    private void PutReference(string fromId, string toId)
    {
        storage.Put($"ref:{fromId}:{toId}", Array.Empty<byte>());
        storage.Put($"refby:{toId}:{fromId}", Array.Empty<byte>());
    }

    private static bool IsType(string assetId, string prefix) => assetId.StartsWith(prefix, StringComparison.Ordinal);

    // ---- names and keys ---------------------------------------------------

    // The key of the Basic Scene in force under a name; null when the Module keeps no Audio Scene of that name.
    public string? SceneKey(string? name) => Names.KeyOf(NamedKind.AudioScene, name);
    public string? SceneName(string? key) => Names.NameOf(key);

    // The key of the Audio Object (Basic, here) that Audio Object Editing keeps under a name.
    public string? ObjectKey(string? name) => Names.KeyOf(NamedKind.AudioObject, name);
    public string? ObjectName(string? key) => Names.NameOf(key);

    // The name designates this version of the Scene (an earlier one put back by a revocation).
    public void Select(string? key)
    {
        if (key is not null && Names.NameOf(key) is { } name) Names.Point(NamedKind.AudioScene, name, key);
    }

    public bool IsBasicObject(string? key) => key is not null && IsType(key, "BAO") && storage.Exists(key);

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

        var key = NextId("BAS");
        var name = Names.Claim(NamedKind.AudioScene, sceneName, key);
        storage.Put(key, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = name,
            BASSpaceTime = Origin(),
            UserPoV = listenerPointOfView,
            AudioObjectCount = 1,
            BasicAudioSceneDescriptorsEntries = new() { MakeBasicEntry(basicAudioObjectKey, placement) }
        }));

        PutReference(key, basicAudioObjectKey);

        return new RepositoryAsset { AssetId = key, AssetType = AssetType.BAS };
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
            AudioObjectIDOrAudioObject = e.AudioObjectIDOrAudioObject, UserPoV = e.UserPoV, AudioObjectSpaceTime = placement ?? e.AudioObjectSpaceTime
        });

    public RepositoryAsset? RemoveFromBasicScene(string sceneKey, string memberName) =>
        RebuildBasicScene(sceneKey, memberName, _ => null);

    private RepositoryAsset? RebuildBasicScene(string sceneKey, string memberName, Func<BasicAudioSceneEntry, BasicAudioSceneEntry?> change)
    {
        var existing = Existing(sceneKey);
        var found = false;
        var entries = new List<BasicAudioSceneEntry>();
        foreach (var e in existing.BasicAudioSceneDescriptorsEntries)
        {
            if (!found && ObjectName(e.AudioObjectIDOrAudioObject?.BasicAudioObjectID) == memberName)
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

    // Sets the acoustics of a Basic scene (the Scene part of the Acoustic Profile): a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneAcousticProfile(string sceneKey, AcousticProfile acousticProfile)
    {
        var existing = Existing(sceneKey);
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, existing.UserPoV, existing.ClosedSpace, acousticProfile);
    }

    // Sets the space a Basic scene is in - a room built round its members: a NEW version, as every edit.
    public RepositoryAsset SetBasicSceneClosedSpace(string sceneKey, System.Text.Json.Nodes.JsonArray closedSpace)
    {
        var existing = Existing(sceneKey);
        return Store(sceneKey, existing, existing.BasicAudioSceneDescriptorsEntries, existing.UserPoV, closedSpace, existing.AcousticProfile);
    }

    private BasicAudioSceneDescriptors Existing(string sceneKey)
    {
        if (!storage.Exists(sceneKey) || !IsType(sceneKey, "BAS"))
            throw new InvalidOperationException("The Module keeps no such Audio Scene.");
        return Deserialize<BasicAudioSceneDescriptors>(storage.Get(sceneKey));
    }

    // The new version of a Scene: a fresh key, which the Scene's name designates from now on; the version it comes from keeps its key and its name.
    private RepositoryAsset Store(string oldKey, BasicAudioSceneDescriptors existing, IEnumerable<BasicAudioSceneEntry> entries, PointOfView? userPoV,
                                  System.Text.Json.Nodes.JsonArray? closedSpace, AcousticProfile? acousticProfile)
    {
        var name = Names.NameOf(oldKey) ?? throw new InvalidOperationException("The Scene has no name.");
        var newKey = NextId("BAS");
        Names.Point(NamedKind.AudioScene, name, newKey);
        var kept = entries.ToList();
        foreach (var e in kept)
            if (e.AudioObjectIDOrAudioObject?.BasicAudioObjectID is { Length: > 0 } member) PutReference(newKey, member);
        storage.Put(newKey, Serialize(new BasicAudioSceneDescriptors
        {
            MInstanceID = mInstanceId,
            BasicAudioSceneDescriptorsID = name,
            BasicAudioSceneDescriptorsTime = existing.BasicAudioSceneDescriptorsTime,
            BASSpaceTime = existing.BASSpaceTime ?? Origin(),
            UserPoV = userPoV,
            ClosedSpace = closedSpace,
            AcousticProfile = acousticProfile,
            AudioObjectCount = kept.Count,
            BasicAudioSceneDescriptorsEntries = kept
        }));
        return new RepositoryAsset { AssetId = newKey, AssetType = AssetType.BAS };
    }

    // ---- reading --------------------------------------------------------------

    public bool Has(string? key) => !string.IsNullOrWhiteSpace(key) && storage.Exists(key);

    // Resolves a stored Basic scene into a fully self-contained structure: its own fields, with every ID-stub BasicAudioObject entry replaced by its full, live content
    // (which carries its name), and the Scene itself named by its name.
    public BasicAudioSceneDescriptors MaterializeBasicScene(string sceneKey)
    {
        var scene = Existing(sceneKey);
        var name = Names.NameOf(sceneKey) ?? throw new InvalidOperationException("The Scene has no name.");

        var resolvedEntries = new List<BasicAudioSceneEntry>();

        foreach (var entry in scene.BasicAudioSceneDescriptorsEntries)
        {
            var childKey = entry.AudioObjectIDOrAudioObject?.BasicAudioObjectID;
            if (string.IsNullOrEmpty(childKey) || !storage.Exists(childKey)) continue;

            var bao = Deserialize<BasicAudioObject>(storage.Get(childKey));

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
            AudioObjectCount = resolvedEntries.Count,
            BasicAudioSceneDescriptorsEntries = resolvedEntries
        };
    }

    // THE LIST (the author, 2026/10/26): the Audio Scenes the Module keeps - the version in force of each - by name, with the names of the Objects each holds. Never a key.
    public IReadOnlyList<ListedObject> Listing(string requested)
    {
        var found = new List<ListedObject>();
        if (!ListKinds.Asks(requested, ListKinds.AudioScene)) return found;
        foreach (var (name, key) in Names.All(NamedKind.AudioScene))
        {
            var members = storage.Exists(key)
                ? Deserialize<BasicAudioSceneDescriptors>(storage.Get(key)).BasicAudioSceneDescriptorsEntries
                    .Select(e => ObjectName(e.AudioObjectIDOrAudioObject?.BasicAudioObjectID)).Where(n => n is not null).Select(n => n!).ToList()
                : null;
            found.Add(new ListedObject { ObjectID = name, Kind = ListKinds.AudioScene, MemberObjectIDs = members });
        }
        return found;
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
