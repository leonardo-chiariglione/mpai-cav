// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;

namespace Mpai.Mmc.Sir;

// The enrolled-speakers database SIR matches against. Enrol a speaker's voice
// embedding under an ID; identify a new utterance by finding the nearest
// enrolled embedding above a cosine-similarity threshold. Direct twin of the
// PAF FaceDatabase, with speaker embeddings in place of face embeddings.
public sealed class SpeakerDatabase
{
    private readonly List<SpeakerRecord> _records = new();

    // Cosine threshold for a positive match. With the 3D-Speaker ECAPA
    // embeddings, same-speaker pairs measured ~0.72 and different-speaker ~0.20
    // on the verification clips, so 0.45 sits comfortably between them. Tune per
    // deployment (enrolment quality, channel conditions) as needed.
    private readonly float _threshold;

    public SpeakerDatabase(float threshold = 0.45f) => _threshold = threshold;

    // Enrol (register) a speaker embedding under a known identity. Multiple
    // enrolments per speaker are allowed (each is a record); Identify takes the
    // best over all records, so more enrolments = more robust.
    public void Enrol(string speakerId, float[] embedding)
        => _records.Add(new SpeakerRecord { SpeakerId = speakerId, Embedding = embedding });

    // Identify a speaker embedding: best match above threshold, or null (unknown).
    public SpeakerMatch? Identify(float[] embedding)
    {
        SpeakerRecord? best = null;
        double bestSim = double.NegativeInfinity;
        foreach (var r in _records)
        {
            double sim = SpeakerEmbedder.Cosine(embedding, r.Embedding);
            if (sim > bestSim) { bestSim = sim; best = r; }
        }
        if (best is null || bestSim < _threshold) return null;
        return new SpeakerMatch { SpeakerId = best.SpeakerId, Similarity = (float)bestSim };
    }

    public int Count => _records.Count;
}

public sealed class SpeakerRecord
{
    public string SpeakerId { get; init; } = "";
    public float[] Embedding { get; init; } = Array.Empty<float>();
}

public sealed class SpeakerMatch
{
    public string SpeakerId { get; init; } = "";
    public float Similarity { get; init; }
}
