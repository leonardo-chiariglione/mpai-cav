using System;
using System.Collections.Generic;
using System.Linq;

namespace Mpai.Cae.Apps;

// The options of an App on its command line: --name followed by its values (one or several, up to the next --), and flags with none.
public sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public Options(string[] args, params string[] known)
    {
        List<string>? current = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                if (!known.Contains(a)) throw new ArgumentException($"I do not know {a}. The options are: {string.Join(" ", known)}.");
                current = _values[a] = [];
            }
            else if (current is not null) current.Add(a);
            else throw new ArgumentException($"'{a}' is not the value of an option.");
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public IReadOnlyList<string> Many(string name) => _values.GetValueOrDefault(name) ?? [];
    public string? One(string name) => _values.TryGetValue(name, out var v) ? (v.Count == 1 ? v[0] : throw new ArgumentException($"{name} takes one value.")) : null;
    public string Need(string name) => One(name) ?? throw new ArgumentException($"{name} is needed.");
}