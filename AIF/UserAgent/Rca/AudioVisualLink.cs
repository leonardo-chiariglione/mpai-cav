using System;
using System.Text.Json;

using Mpai.Ua;
namespace Mpai.Rca;

// THE VIDEO A BASIC AUDIOVISUAL OBJECT POINTS AT (OSD-BMO-V1.5). Its data is not carried: BasicAudioVisualObjectData holds a DataURI, and the
// User Agent, which has the screen and the loudspeakers, plays what the URI designates. A video of YouTube is played by the YouTube player
// (its embedded player, in a page the client shows on the stage); any other URI is played by the client's own video element.
public static class AudioVisualLink
{
    // The URI of the first data item that has one; null when the object has none.
    public static string? UriOf(string audioVisualObjectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(audioVisualObjectJson);
            if (!doc.RootElement.TryGetProperty("BasicAudioVisualObjectData", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("DataURI", out var uri) && uri.GetString() is { Length: > 0 } s) return s;
        }
        catch (JsonException) { }
        return null;
    }

    // The identifier of a YouTube video from its link (youtu.be/ID, youtube.com/watch?v=ID, /embed/ID, /shorts/ID); null for any other link.
    public static string? YouTubeId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        if (host == "youtu.be") return Clean(u.AbsolutePath.Trim('/'));
        if (host == "youtube.com" || host.EndsWith(".youtube.com"))
        {
            foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (pair.StartsWith("v=", StringComparison.Ordinal)) return Clean(pair[2..]);
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 2 && (parts[0] == "embed" || parts[0] == "shorts" || parts[0] == "v")) return Clean(parts[1]);
        }
        return null;
    }

    private static string? Clean(string id)
    {
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) return null;
        return id.Length is >= 6 and <= 20 ? id : null;
    }
}
