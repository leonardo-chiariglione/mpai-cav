using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// WHAT THE MPAI STORE SERVICE ISSUED, kept on disk (<folder>/<id>.json, with an index): one AIM Licence per AIM for a holder of a Trust Anchor - and, where only one Controller is
// licensed, for that Controller - so that what is paid for once is not paid for again, even if the Store is restarted; and what the Store revoked, with the reason and the time.
public sealed class LicenceBook
{
    private readonly string folder;
    private readonly object gate = new();
    private readonly List<Entry> entries = [];

    private sealed record Entry(string Id, string AimName, string TrustAnchorId, string? ControllerId);

    public LicenceBook(string folder)
    {
        this.folder = folder;
        Directory.CreateDirectory(folder);
        if (File.Exists(IndexFile))
            foreach (var item in JsonNode.Parse(File.ReadAllText(IndexFile))!.AsArray())
                entries.Add(new Entry((string)item!["Id"]!, (string)item["AIMName"]!, (string)item["TrustAnchorID"]!, (string?)item["ControllerID"]));
    }

    private string IndexFile => Path.Combine(folder, "index.json");
    private string LicenceFile(string id) => Path.Combine(folder, id + ".json");
    private string RevokedFile(string id) => Path.Combine(folder, id + ".revoked.json");

    public int Count { get { lock (gate) return entries.Count; } }

    // The AIM Licence for the AIM and the holder (and Controller, if one is named) - the one issued before, or the one issue() makes now. Created says which.
    public (string Licence, bool Created) GetOrIssue(string aimName, string trustAnchorId, string? controllerId, Func<JsonObject> issue)
    {
        lock (gate)
        {
            var found = entries.FirstOrDefault(e => e.AimName == aimName && e.TrustAnchorId == trustAnchorId && e.ControllerId == controllerId);
            if (found is not null) return (File.ReadAllText(LicenceFile(found.Id)), false);
            var licence = issue();
            var id = (string)licence["AIMLicenceID"]!;
            File.WriteAllText(LicenceFile(id), licence.ToJsonString());
            entries.Add(new Entry(id, aimName, trustAnchorId, controllerId));
            File.WriteAllText(IndexFile, new JsonArray(entries.Select(e => (JsonNode)new JsonObject
                { ["Id"] = e.Id, ["AIMName"] = e.AimName, ["TrustAnchorID"] = e.TrustAnchorId, ["ControllerID"] = e.ControllerId }).ToArray()).ToJsonString());
            return (licence.ToJsonString(), true);
        }
    }

    // The AIM Licence with that identifier, as the Store issued it; null if it issued none.
    public string? Text(string id)
    {
        lock (gate) return entries.Any(e => e.Id == id) ? File.ReadAllText(LicenceFile(id)) : null;
    }

    // Whether it is valid or revoked, and the reason; null if the Store issued no AIM Licence with that identifier.
    public (string Status, string? Reason, string? Time)? Status(string id)
    {
        lock (gate)
        {
            if (!entries.Any(e => e.Id == id)) return null;
            if (!File.Exists(RevokedFile(id))) return ("valid", null, null);
            var revoked = JsonNode.Parse(File.ReadAllText(RevokedFile(id)))!;
            return ("revoked", (string?)revoked["Reason"], (string?)revoked["Time"]);
        }
    }

    // Revokes it (the Store operator's act). False if the Store issued no AIM Licence with that identifier.
    public bool Revoke(string id, string reason, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!entries.Any(e => e.Id == id)) return false;
            File.WriteAllText(RevokedFile(id), new JsonObject { ["Reason"] = reason, ["Time"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") }.ToJsonString());
            return true;
        }
    }
}
