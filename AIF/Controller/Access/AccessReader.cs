using System.Text.Json.Nodes;

using AIF.Channels;

namespace AIF.Controller;

// WHAT AN AIM HAS OF ACCESS (AIF V3.0, Basic API 4.11.3): reading, and nothing else -
// MPAI_AIFM_Access_Get, _List, _Version. An AIM is given this, never the store.
public interface IAccessReader
{
    AccessOutcome Get(string source, string key, out byte[] data);
    IReadOnlyList<string> List(string source, string prefix = "");
    long Version(string source);

    // The item as a stream, for what is too large to be held in memory. Where the Access is not on the same machine as
    // the AIM it is the data of Get.
    AccessOutcome Open(string source, string key, out Stream stream)
    {
        var outcome = Get(source, key, out var data);
        stream = new MemoryStream(data, false);
        return outcome;
    }

    // The item as a file, for a program that reads files - a runtime, a speech engine - and cannot be given a stream. Where
    // the Access is kept on the same machine it is the file where the item is kept, to be read and not written; otherwise
    // a copy made for the reader.
    AccessOutcome LocalPath(string source, string key, out string path)
    {
        path = "";
        var outcome = Open(source, key, out var stream);
        if (outcome != AccessOutcome.OK) return outcome;
        path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N") + ".data");
        using (stream) using (var to = File.Create(path)) stream.CopyTo(to);
        return AccessOutcome.OK;
    }

    // The Sources (URIs) the AIM declared under a name; every one when the name is empty. Only the narrowed Access of
    // an AIM (BoundAccess) knows them.
    IReadOnlyList<string> SourcesOf(string name) => [];
}

// ACCESS FOR AN AIM ON AN AIM HOST: Access is the Controller's, so an AIM placed on
// a host reads it through the Controller, each call a request on the link the
// Controller opened to the host - admitted as that link is. The Controller answers
// only for a Module it placed there, and only reads.
public sealed class RemoteAccess(RemoteLink link, string module, string aim = "") : IAccessReader
{
    // The same link, asking for one AIM: the Controller applies the rules of each datum to that AIM.
    public RemoteAccess ForAim(string aimName) => new(link, module, aimName);

    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private JsonObject? Ask(JsonObject request)
    {
        request["Kind"] = "Access";
        request["Module"] = module;
        request["Aim"] = aim;
        try { return link.RequestAsync(request).WaitAsync(Wait).GetAwaiter().GetResult(); }
        catch { return null; }                                          // the link gone, or too slow
    }

    public AccessOutcome Get(string source, string key, out byte[] data)
    {
        data = [];
        var reply = Ask(new JsonObject { ["Function"] = "Get", ["Source"] = source, ["Key"] = key });
        if (reply is null || !Enum.TryParse<AccessOutcome>((string?)reply["Outcome"], out var outcome)) return AccessOutcome.NotFound;
        if (outcome == AccessOutcome.OK) data = Convert.FromBase64String((string?)reply["Data"] ?? "");
        return outcome;
    }

    // The item as a file, for a program that reads files: where the host can see the file the Controller keeps it in (the same machine, or a shared
    // disk) that is the file, to be read and not written; otherwise a copy, made here.
    public AccessOutcome LocalPath(string source, string key, out string path)
    {
        path = "";
        var reply = Ask(new JsonObject { ["Function"] = "Path", ["Source"] = source, ["Key"] = key });
        if (reply is null || !Enum.TryParse<AccessOutcome>((string?)reply["Outcome"], out var outcome)) return AccessOutcome.NotFound;
        if (outcome != AccessOutcome.OK) return outcome;
        if ((string?)reply["Path"] is { Length: > 0 } there && File.Exists(there)) { path = there; return AccessOutcome.OK; }
        var copied = Get(source, key, out var data);
        if (copied != AccessOutcome.OK) return copied;
        path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpai-access-" + Guid.NewGuid().ToString("N") + ".data");
        File.WriteAllBytes(path, data);
        return AccessOutcome.OK;
    }

    public IReadOnlyList<string> List(string source, string prefix = "") =>
        Ask(new JsonObject { ["Function"] = "List", ["Source"] = source, ["Prefix"] = prefix })?["Keys"] is JsonArray keys
            ? keys.Select(k => (string)k!).ToList()
            : [];

    public long Version(string source) =>
        Ask(new JsonObject { ["Function"] = "Version", ["Source"] = source })?["Version"]?.GetValue<long>() ?? -1;

    // The Controller's answer to such a request, from its Access (null: it has none).
    public static JsonObject Answer(IAccessReader? access, JsonObject request)
    {
        var source = (string?)request["Source"] ?? "";
        switch ((string?)request["Function"])
        {
            case "Get":
                if (access is null) return new JsonObject { ["Ok"] = false, ["Outcome"] = nameof(AccessOutcome.NotFound) };
                var outcome = access.Get(source, (string?)request["Key"] ?? "", out var data);
                var reply = new JsonObject { ["Ok"] = outcome == AccessOutcome.OK, ["Outcome"] = outcome.ToString() };
                if (outcome == AccessOutcome.OK) reply["Data"] = Convert.ToBase64String(data);
                return reply;
            case "List":
                return new JsonObject
                {
                    ["Ok"] = true,
                    ["Keys"] = new JsonArray((access?.List(source, (string?)request["Prefix"] ?? "") ?? []).Select(k => (JsonNode?)k).ToArray())
                };
            case "Version":
                return new JsonObject { ["Ok"] = true, ["Version"] = access?.Version(source) ?? -1 };
            case "Path":
                if (access is null) return new JsonObject { ["Ok"] = false, ["Outcome"] = nameof(AccessOutcome.NotFound) };
                var found = access.LocalPath(source, (string?)request["Key"] ?? "", out var path);
                return new JsonObject { ["Ok"] = found == AccessOutcome.OK, ["Outcome"] = found.ToString(), ["Path"] = found == AccessOutcome.OK ? path : "" };
            default:
                return new JsonObject { ["Ok"] = false, ["Outcome"] = nameof(AccessOutcome.Refused), ["Error"] = "Access is only read" };
        }
    }
}
