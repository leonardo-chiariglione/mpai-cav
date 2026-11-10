using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Paf.Ebd;

namespace Mpai.Paf.Pbi;

// PAF-PBI-V1.6 - PS-Body Interpretation. Receives the Body Descriptors Object of a body (its 33 keypoints, from Entity Body Description) and gives the Body Personal Status.
//
// FIRST IMPLEMENTATION, BY SIMPLE RULES. No model reads the Personal Status from the keypoints: a few postures and gestures are recognised from the geometry of the keypoints, and each is given
// a label of the vocabulary. The rules are a first reading of common conventions (raised arms, hands near the face, crossed arms, hands on the hips, open arms, head dropped), not a result
// of a study of the body language of the Entities, and they say nothing about a posture that is not among them. Where the picture does not show the keypoints a rule needs (visibility
// below 0.5), the rule is not applied. A Factor takes the rule with the highest Degree among those that apply; a Factor that no rule gives is left out.
//
//   RULE                                                  FACTOR            LABEL                                                              DEGREE
//   both wrists above the nose (arms raised)              Emotion           HAPPINESS / happy                                                  how far above the nose, in torso lengths (cut at 1), times 0.7
//                                                         Cognitive State   AROUSAL / aroused/excited/energetic                                how far above the nose, in torso lengths (cut at 1)
//   a wrist near the nose (hand at the face)              Cognitive State   ATTENTION / attentive / thoughtful                                 1 - distance / (half the shoulder width)
//   wrists crossed over the chest (arms crossed)          Social Attitude   OPENNESS/TRUST / closed/distant                                    how far each wrist is across the midline, in half shoulder widths (cut at 1)
//   each wrist near the hip of its side, the elbow out to   Social Attitude   SOCIAL DOMINANCE/CONFIDENCE / confident                              1 - distance / (a third of the torso)
//     the side (0.4 shoulder widths beyond the shoulder)
//     (hands on hips; hanging arms are told apart by the elbows)
//   both wrists far from the midline at the height of     Social Attitude   OPENNESS/TRUST / open                                              how far beyond 1.3 shoulder widths, in shoulder widths (cut at 1)
//     the shoulders (arms open)
//   the nose at or below the shoulders (head dropped)     Emotion           SADNESS / sad                                                      how far below the line 0.15 torso lengths above the shoulders, in torso lengths (cut at 1)
public sealed class PbiAimProcessor : IAimProcessor
{
    public const float MinVisibility = 0.5f;

    private readonly string _firstPort, _secondPort, _outPort;
    public string InstanceId { get; }

    public PbiAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _firstPort = ports.Input("PAF-BDO-V1.6", 1);
        _secondPort = ports.InputOrDefault("PAF-BDO-V1.6", 2, "");
        _outPort = ports.Output("MMC-BPS-V2.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        string? json = null;
        foreach (var port in new[] { _firstPort, _secondPort })
            if (port.Length > 0 && message.Ports.TryGetValue(port, out var j) && !string.IsNullOrWhiteSpace(j)) { json = j; break; }
        if (json is null) return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-PBI-V1.6: no Body Descriptors Object to interpret."));
        float[] keypoints;
        try { keypoints = EbdAimProcessor.Keypoints(json); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-PBI-V1.6: " + ex.Message)); }
        var outJson = MpaiJson.ToJson(Interpret(keypoints));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }

    private readonly record struct Candidate(string Factor, FactorLabel Label);

    // The Body Personal Status of 33 keypoints, each x, y, z, visibility (the y axis goes down).
    public static BodyPersonalStatus Interpret(float[] k)
    {
        (float X, float Y, float V) P(int i) => (k[i * 4], k[i * 4 + 1], k[i * 4 + 3]);
        bool Seen(params int[] i) => i.All(n => P(n).V >= MinVisibility);
        float Dist((float X, float Y, float V) a, (float X, float Y, float V) b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

        var found = new List<Candidate>();
        const int nose = PoseLandmarker.Nose, ls = PoseLandmarker.LeftShoulder, rs = PoseLandmarker.RightShoulder, lw = PoseLandmarker.LeftWrist, rw = PoseLandmarker.RightWrist,
                  lh = PoseLandmarker.LeftHip, rh = PoseLandmarker.RightHip, le = PoseLandmarker.LeftElbow, re = PoseLandmarker.RightElbow;
        if (!Seen(ls, rs)) return new BodyPersonalStatus();
        var midS = ((P(ls).X + P(rs).X) / 2, (P(ls).Y + P(rs).Y) / 2, 1f);
        float sw = Dist(P(ls), P(rs));                                           // shoulder width
        float torso = Seen(lh, rh) ? Dist(midS, ((P(lh).X + P(rh).X) / 2, (P(lh).Y + P(rh).Y) / 2, 1f)) : 1.3f * sw;   // torso length; if the hips are not seen, 1.3 shoulder widths
        if (sw < 1e-3f || torso < 1e-3f) return new BodyPersonalStatus();
        float midX = midS.Item1;

        // Arms raised.
        if (Seen(nose, lw, rw) && P(lw).Y < P(nose).Y && P(rw).Y < P(nose).Y)
        {
            float d = Clamp01(((P(nose).Y - P(lw).Y) + (P(nose).Y - P(rw).Y)) / 2 / torso);
            found.Add(new("E", FactorLabel.Of("HAPPINESS", "happy", null, 0.7 * d)));
            found.Add(new("C", FactorLabel.Of("AROUSAL", "aroused/excited/energetic", null, d)));
        }
        // Hand at the face.
        if (Seen(nose))
            foreach (var w in new[] { lw, rw })
                if (Seen(w) && Dist(P(w), P(nose)) < 0.5f * sw)
                    found.Add(new("C", FactorLabel.Of("ATTENTION", "attentive", "thoughtful", Clamp01(1 - Dist(P(w), P(nose)) / (0.5f * sw)))));
        // Arms crossed: each wrist is on the side of the other shoulder, at the height of the chest.
        if (Seen(lw, rw))
        {
            float cl = -(P(lw).X - midX) * MathF.Sign(P(ls).X - midX), cr = -(P(rw).X - midX) * MathF.Sign(P(rs).X - midX);
            bool chest = P(lw).Y > midS.Item2 && P(rw).Y > midS.Item2 && P(lw).Y < midS.Item2 + torso && P(rw).Y < midS.Item2 + torso;
            if (cl > 0 && cr > 0 && chest)
                found.Add(new("S", FactorLabel.Of("OPENNESS/TRUST", "closed/distant", null, Clamp01(Math.Min(cl, cr) / (0.5f * sw)))));
        }
        // Hands on the hips.
        if (Seen(lw, rw, lh, rh, le, re) && Dist(P(lw), P(lh)) < torso / 3 && Dist(P(rw), P(rh)) < torso / 3
            && Math.Abs(P(le).X - midX) > Math.Abs(P(ls).X - midX) + 0.4f * sw && Math.Abs(P(re).X - midX) > Math.Abs(P(rs).X - midX) + 0.4f * sw)
            found.Add(new("S", FactorLabel.Of("SOCIAL DOMINANCE/CONFIDENCE", "confident", null, Clamp01(1 - Math.Max(Dist(P(lw), P(lh)), Dist(P(rw), P(rh))) / (torso / 3)))));
        // Arms open.
        if (Seen(lw, rw) && Math.Abs(P(lw).X - midX) > 1.3f * sw && Math.Abs(P(rw).X - midX) > 1.3f * sw && Math.Abs(P(lw).Y - midS.Item2) < 0.5f * torso && Math.Abs(P(rw).Y - midS.Item2) < 0.5f * torso
            && (P(lw).X - midX) * (P(rw).X - midX) < 0)
            found.Add(new("S", FactorLabel.Of("OPENNESS/TRUST", "open", null, Clamp01((Math.Min(Math.Abs(P(lw).X - midX), Math.Abs(P(rw).X - midX)) - 1.3f * sw) / sw))));
        // Head dropped.
        if (Seen(nose) && P(nose).Y > midS.Item2 - 0.15f * torso)
            found.Add(new("E", FactorLabel.Of("SADNESS", "sad", null, Clamp01((P(nose).Y - (midS.Item2 - 0.15f * torso)) / torso))));

        FactorLabel? Best(string f) => found.Where(c => c.Factor == f).OrderByDescending(c => c.Label.Degree).Select(c => c.Label).FirstOrDefault();
        var e = Best("E"); var c = Best("C"); var s = Best("S");
        return new BodyPersonalStatus
        {
            BodyEmotion = e is null ? null : Emotion.Of(e),
            BodyCognitiveState = c is null ? null : CognitiveState.Of(c),
            BodySocialAttitude = s is null ? null : SocialAttitude.Of(s)
        };
    }
}

public sealed class PbiPlugin : IAimPlugin
{
    public string AimName => "PAF-PBI-V1.6";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new PbiAimProcessor(AimName, ports);
}
