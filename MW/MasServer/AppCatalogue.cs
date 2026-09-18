using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mpai.Mas.Server;

// WHAT THIS SERVICE OFFERS. An App is a Workflow Description and the few things
// a person needs in order to choose it: a name, a sentence saying what it does,
// and a picture.
//
// A client holds no application. It arrives knowing only how to render an
// avatar, capture speech and interpret a Workflow Description, asks a Service
// what it has, and runs what the user picks. Which application it runs is
// decided by a document, at the moment of choosing.
//
// One folder per App, under the directory the Service is configured with:
//
//     Apps/
//       AMQ/
//         app.json        { "Name": ..., "Description": ..., "Icon": ... }
//         MMC-AMQ.orch
//         icon.png
//
// The folder name is the App's identifier. Nothing here reads the workflow: the
// Service serves it as text and the client interprets it, which is what keeps
// the Service free of any knowledge of what its Apps do.
public sealed class AppCatalogue
{
    public sealed record Entry(
        string  Id,
        string  Name,
        string  Description,
        string? IconFile,
        string  WorkflowPath,
        string  Folder);

    private readonly Dictionary<string, Entry> byId =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Entry> Apps => byId.Values;
    public string? Root { get; }

    private AppCatalogue(string? root) { Root = root; }

    // Read once, at startup. An App added later needs a restart, which is honest
    // for a Service that states what it has when it starts.
    public static AppCatalogue Scan(string? root)
    {
        var catalogue = new AppCatalogue(root);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return catalogue;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var id   = Path.GetFileName(folder);
            var orch = Directory.EnumerateFiles(folder, "*.orch").FirstOrDefault();
            if (orch is null) continue;          // a folder with no workflow is not an App

            string name = id, description = "", icon = "";
            var manifest = Path.Combine(folder, "app.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("Name", out var n))        name        = n.GetString() ?? id;
                    if (r.TryGetProperty("Description", out var d)) description = d.GetString() ?? "";
                    if (r.TryGetProperty("Icon", out var i))        icon        = i.GetString() ?? "";
                }
                catch
                {
                    // A manifest that will not parse leaves the App listed under its
                    // folder name. Better a nameless App than a missing one.
                }
            }

            var iconPath = string.IsNullOrWhiteSpace(icon) ? null : Path.Combine(folder, icon);
            if (iconPath is not null && !File.Exists(iconPath)) iconPath = null;

            catalogue.byId[id] = new Entry(id, name, description,
                                 iconPath is null ? null : Path.GetFileName(iconPath),
                                 orch, folder);
        }
        return catalogue;
    }

    public Entry? Find(string id) => byId.TryGetValue(id, out var e) ? e : null;

    // The catalogue as a client receives it. The icon is named, not embedded: a
    // client showing a list fetches only the few it displays.
    public string ToJson() =>
        JsonSerializer.Serialize(
            byId.Values.Select(a => new
            {
                id          = a.Id,
                name        = a.Name,
                description = a.Description,
                icon        = a.IconFile is null ? null : $"Apps/{a.Id}/Icon",
                workflow    = $"Apps/{a.Id}"
            }),
            new JsonSerializerOptions { WriteIndented = true });
}