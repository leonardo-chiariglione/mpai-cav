using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using AIF.SharedStorage;

namespace Mpai.Core;

// THE FORMS OF CAE-UCM THAT ARE NOT THE PLAIN OBJECT (the author, 2026/10/28; M3282):
//
//   A Spatial Attitude in full, or only the parts that change - Position, Orientation, SpatialAttitudeTime; what is not stated keeps its value (UCM-1).
//   An Acoustic Profile in full, or the AcousticProfileID of a kept one (UCM-4), as ClosedSpace is a Closed Space in full or by ID.

// A Spatial Attitude as a command carries it. A command that states only some of the parts leaves the others false here (PositionStated, OrientationStated); a Spatial Attitude built by
// code states both, as it always did.
public sealed class SpatialAttitudeOrChangeConverter : JsonConverter<SpatialAttitude>
{
    public override SpatialAttitude? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject node) return null;
        var read = node.Deserialize<SpatialAttitude>(options) ?? new SpatialAttitude();
        return new SpatialAttitude
        {
            Header = read.Header, MInstanceID = read.MInstanceID, UEnvironmentID = read.UEnvironmentID, ObjectSpatialAttitudeID = read.ObjectSpatialAttitudeID,
            SpatialAttitudeTime = read.SpatialAttitudeTime, General = read.General, Position = read.Position, Orientation = read.Orientation,
            DataXMData = read.DataXMData, DescrMetadata = read.DescrMetadata,
            PositionStated = node.ContainsKey("Position"), OrientationStated = node.ContainsKey("Orientation")
        };
    }

    public override void Write(Utf8JsonWriter writer, SpatialAttitude value, JsonSerializerOptions options)
    {
        if (value.PositionStated && value.OrientationStated) { JsonSerializer.Serialize(writer, value, options); return; }
        var full = JsonSerializer.SerializeToNode(value, options) as JsonObject ?? new JsonObject();
        var partial = new JsonObject();
        if (value.PositionStated) partial["Position"] = full["Position"]?.DeepClone();
        if (value.OrientationStated) partial["Orientation"] = full["Orientation"]?.DeepClone();
        if (full["SpatialAttitudeTime"] is { } time) partial["SpatialAttitudeTime"] = time.DeepClone();
        partial.WriteTo(writer);
    }
}

// An Acoustic Profile in full, or the identifier of a kept one (a string): the second is a profile with only its AcousticProfileID, and Reference.
public sealed class AcousticProfileOrIdConverter : JsonConverter<AcousticProfile>
{
    public override AcousticProfile? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? new AcousticProfile { AcousticProfileID = reader.GetString() ?? "", Reference = true }
            : JsonSerializer.Deserialize<AcousticProfile>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, AcousticProfile value, JsonSerializerOptions options)
    {
        if (value.Reference) writer.WriteStringValue(value.AcousticProfileID);
        else JsonSerializer.Serialize(writer, value, options);
    }
}

// THE ACOUSTIC PROFILES THE MODULE KEEPS, by their AcousticProfileID: a profile given in full is kept under its identifier (a profile with the same identifier is replaced); a command
// may then give the identifier alone. Listed by Audio Scene Editing (Kind Acoustic Profile, no ObjectID); deleted by DeletedObjects with an AcousticProfileID.
public sealed class KeptProfiles(ModuleStore store)
{
    private static string IdOf(string id) => id.Trim();

    // Keeps the profile under its identifier (CAE-ACP-V1.0#1/<AcousticProfileID>@<version>: a profile given again is a further version, the highest is in force); false when it has none
    // (nothing to keep it under).
    public bool Keep(AcousticProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.AcousticProfileID) || profile.Reference) return false;
        var id = IdOf(profile.AcousticProfileID);
        var bytes = Encoding.UTF8.GetBytes(MpaiJson.ToJson(profile));
        var latest = store.Latest(Kinds.AcousticProfile, id);
        if (latest > 0 && store.Get(Kinds.AcousticProfile, id)!.AsSpan().SequenceEqual(bytes)) return true;
        store.Put(Kinds.AcousticProfile, id, latest + 1, bytes);
        return true;
    }

    public AcousticProfile? Get(string? id) =>
        !string.IsNullOrWhiteSpace(id) && store.Get(Kinds.AcousticProfile, IdOf(id)) is { } bytes
            ? MpaiJson.FromJson<AcousticProfile>(Encoding.UTF8.GetString(bytes)) : null;

    // False when none is kept; a profile that another AIM of the Module wrote is not deleted by this one (UnauthorizedAccessException).
    public bool Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !store.Holds(Kinds.AcousticProfile, IdOf(id))) return false;
        store.Delete(Kinds.AcousticProfile, IdOf(id));
        return true;
    }

    public IReadOnlyList<AcousticProfile> All() =>
        store.All(Kinds.AcousticProfile)
            .Select(p => MpaiJson.FromJson<AcousticProfile>(Encoding.UTF8.GetString(store.Get(Kinds.AcousticProfile, p.Id)!)))
            .OrderBy(p => p.AcousticProfileID, StringComparer.Ordinal).ToList();

    // The profile a command gives: the kept one when it gives an identifier alone (null, with the reason, when none is kept); else the profile given, which is kept under its identifier.
    public (AcousticProfile? Profile, string? Problem) Resolve(AcousticProfile given)
    {
        if (given.Reference)
            return Get(given.AcousticProfileID) is { } kept ? (kept, null) : (null, $"the Module keeps no Acoustic Profile named \"{given.AcousticProfileID}\"");
        Keep(given);
        return (given, null);
    }
}
