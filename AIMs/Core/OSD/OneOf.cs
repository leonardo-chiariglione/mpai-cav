using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mpai.Core.OSD;

// "THE ID OF AN OBJECT OR THE OBJECT" - the schemas' xxxIDOrxxx members, which hold an
// array of one: a Basic Object or an Object (or a Basic Scene or a Scene), or its
// identifier. The Data Types that hold them keep each alternative apart, and use these
// to write and to read the one that is held; which one it is, its Header says.
internal static class OneOf
{
    // The array of one that holds the first of these that is given: an Object or a Scene
    // as itself, else the identifier.
    public static JsonArray? Held(object? first, object? second, string? id)
    {
        var held = first ?? second;
        if (held is not null) return [JsonNode.Parse(MpaiJson.ToJson(held))];
        return string.IsNullOrWhiteSpace(id) ? null : [JsonValue.Create(id)];
    }

    // The held Object or Scene when its Header is this one; null otherwise.
    public static T? Take<T>(JsonArray? held, string header) where T : class =>
        held?.FirstOrDefault() is JsonObject o && o["Header"]?.GetValue<string>() == header
            ? MpaiJson.FromJson<T>(o.ToJsonString()) : null;

    // The identifier, when what is held is an identifier.
    public static string? Id(JsonArray? held) =>
        held?.FirstOrDefault() is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
}
