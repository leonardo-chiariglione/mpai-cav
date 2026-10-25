// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Mpai.Core;

// A POPULATION OF PEOPLE FOR RECOGNITION TO BE MEASURED AGAINST: one feature vector per person, already computed - not the
// images or the speech they came from - kept in a file that is read from Access and never changed. The file is one line of
// JSON (the header) and then, per person, the length of the person's identifier, the identifier (UTF-8) and the vector
// (32-bit floats, little-endian).
//
//   Kind       "face" or "voice": which kind of vector it is
//   Model      the model that computed the vectors (a vector means nothing with another model)
//   Dimension  the number of values of a vector
//   Measure    how two vectors are compared ("cosine")
//   Count      the number of people
//   Source     where the people come from, for people to read
//
// The header states what the file is; whether the model of the AIM is the one that made it is for the Metadata to say
// (ValidWith in AccessSources).
public sealed record ReferenceFeaturesHeader(string Kind, string Model, int Dimension, string Measure, int Count, string Source = "");

public static class ReferenceFeatures
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void Write(Stream to, ReferenceFeaturesHeader header, IReadOnlyList<(string Id, float[] Vector)> people)
    {
        if (people.Count != header.Count) throw new ArgumentException("Count is not the number of people.");
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header) + "\n");
        to.Write(line);
        using var w = new BinaryWriter(to, Encoding.UTF8, leaveOpen: true);
        foreach (var (id, vector) in people)
        {
            if (vector.Length != header.Dimension) throw new ArgumentException($"{id}: the vector has {vector.Length} values, not {header.Dimension}.");
            var name = Encoding.UTF8.GetBytes(id);
            w.Write(name.Length);
            w.Write(name);
            foreach (var x in vector) w.Write(x);
        }
    }

    public static ReferenceFeaturesHeader ReadHeader(Stream from)
    {
        var line = new List<byte>();
        int b;
        while ((b = from.ReadByte()) >= 0 && b != '\n') line.Add((byte)b);
        var header = JsonSerializer.Deserialize<ReferenceFeaturesHeader>(Encoding.UTF8.GetString(line.ToArray()), Json)
                     ?? throw new InvalidDataException("The header of the reference features is empty.");
        if (header.Kind is not ("face" or "voice") || header.Dimension <= 0 || header.Count < 0)
            throw new InvalidDataException("The header of the reference features is not valid.");
        return header;
    }

    public static IEnumerable<(string Id, float[] Vector)> ReadPeople(Stream from, ReferenceFeaturesHeader header)
    {
        using var r = new BinaryReader(from, Encoding.UTF8, leaveOpen: true);
        for (var i = 0; i < header.Count; i++)
        {
            var id = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
            var vector = new float[header.Dimension];
            for (var k = 0; k < vector.Length; k++) vector[k] = r.ReadSingle();
            yield return (id, vector);
        }
    }
}
