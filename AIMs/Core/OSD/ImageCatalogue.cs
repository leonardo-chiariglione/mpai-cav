using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mpai.Core;

// ---------------------------------------------------------------------------
//  ImageCatalogue - the pictures a Service offers to the persons who use it
//  (Route 2 of "ask about a picture": the person chooses one from the catalogue
//  instead of providing one of their own).
//
//  It is a Controller-side component: it lists what is offered and supplies a
//  picture, as a Basic Visual Object, from the name that was chosen. It holds
//  nothing for any person: the picture a person chose belongs to that person's
//  session, which the Controller keeps (one at a time, a new one replaces the
//  old), and it never leaves that session.
//
//  Only the collections named at construction are offered (a deployment's
//  decision - for instance, because of the licence of the pictures or because
//  they show persons). A name never leaves the collections' folders.
//  A name is the path of the picture inside the catalogue, with "/":
//  "Monuments/eiffel-tower/01-pixabay-1853365.jpg". A folder's name stands for
//  the first picture in it.
// ---------------------------------------------------------------------------
public sealed class ImageCatalogue
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    private readonly string _root;
    private readonly string[] _collections;

    public ImageCatalogue(string root, IEnumerable<string> collections)
    {
        _root = Path.GetFullPath(root);
        _collections = collections.ToArray();
    }

    // The collections offered that exist.
    public IReadOnlyList<string> Collections() =>
        _collections.Where(c => Directory.Exists(Path.Combine(_root, c))).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // The names of the pictures in a collection (all collections when null), in a stable order.
    public IReadOnlyList<string> List(string? collection = null)
    {
        var names = new List<string>();
        foreach (var c in Collections())
        {
            if (collection is not null && !string.Equals(c, collection, StringComparison.OrdinalIgnoreCase)) continue;
            var folder = Path.Combine(_root, c);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    names.Add(Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // The full name of the picture a name stands for, or null if the catalogue offers nothing by that name.
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var wanted = name.Trim().Replace(Path.DirectorySeparatorChar, '/').Trim('/');
        var all = List();
        var exact = all.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return all.FirstOrDefault(n => n.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase));
    }

    // The picture, as a Basic Visual Object, or null if the catalogue offers nothing by that name.
    public BasicVisualObject? Supply(string? name)
    {
        var full = Resolve(name);
        if (full is null) return null;
        var path = Path.GetFullPath(Path.Combine(_root, full));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return BasicVisualObject.FromFile(Path.GetFileName(path), File.ReadAllBytes(path), "Picture");
    }
}
