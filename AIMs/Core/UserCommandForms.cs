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
public sealed class KeptProfiles(ISharedStorage storage)
{
    private const string Prefix = "acp:";

    private static string KeyOf(string id) => Prefix + id.Trim();

    // Keeps the profile under its identifier; false when it has none (nothing to keep it under).
    public bool Keep(AcousticProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.AcousticProfileID) || profile.Reference) return false;
        storage.MPAI_AIFM_SharedStorage_Put(KeyOf(profile.AcousticProfileID), Encoding.UTF8.GetBytes(MpaiJson.ToJson(profile)));
        return true;
    }

    public AcousticProfile? Get(string? id) =>
        !string.IsNullOrWhiteSpace(id) && storage.MPAI_AIFM_SharedStorage_Exists(KeyOf(id))
            ? MpaiJson.FromJson<AcousticProfile>(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(KeyOf(id)))) : null;

    public bool Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !storage.MPAI_AIFM_SharedStorage_Exists(KeyOf(id))) return false;
        storage.MPAI_AIFM_SharedStorage_Delete(KeyOf(id));
        return true;
    }

    public IReadOnlyList<AcousticProfile> All() =>
        storage.MPAI_AIFM_SharedStorage_List(Prefix)
            .Select(k => MpaiJson.FromJson<AcousticProfile>(Encoding.UTF8.GetString(storage.MPAI_AIFM_SharedStorage_Get(k))))
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
