using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Ara;

// AIH-ARA-V1.1 - Anomaly and Risk Alerting. "This AIM checks whether AIH Data signal anomalies or risks": AIH Data in, ARA Data (AIH-ARD-V1.1) out.
//
// What the specification gives is the Ports and the Data Types; how the check is made is the implementer's. This one is a rule: the Health Data is read as an
// electrocardiogram (16-bit samples, little endian, at the sampling frequency its qualifier states, else 250 Hz); the beats are found as the peaks that rise
// above 60% of the strongest, at least 250 ms apart; the heart rate is that of the median interval between them. A rate outside what is usual, or beats that
// are not regular, is a risk; the taxonomy's colours say how serious (Red, Orange, Yellow, Green); Blue says that nothing could be judged.
public sealed class AraAimProcessor : IAimProcessor
{
    private readonly string _in, _out;

    public string InstanceId { get; }

    public AraAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _in  = ports.Input(AihJson.AihData);
        _out = ports.Output(AihJson.AraData);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_in, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-ARA-V1.1: no AIH Data on input port '{_in}'."));
        try
        {
            var data = AihJson.Parse(json, "AIH Data");
            var id = data["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the AIH Data has no AIHDataID.");
            // AIH Data that carries its ARA Data has been checked: it is not checked again (the Module's wiring returns it here).
            if (data["AIHData"]?["ARAData"] is not null)
                return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "ARAData", DataType = AihJson.AraData });
            var (risk, anomaly) = Assess(data["AIHData"]?["HealthData"] as JsonObject);
            var result = new JsonObject
            {
                ["Header"] = AihJson.AraData, ["ARADataID"] = AihJson.NewId("ara"), ["ARADataTime"] = AihJson.Time(),
                ["UserID"] = data["UserID"]?.GetValue<string>(),
                ["ARAData"] = new JsonObject { ["AIHDataID"] = id, ["Risk"] = risk }
            };
            if (anomaly is not null) result["ARAData"]!["Anomalies"] = new JsonObject { ["Anomaly"] = anomaly };
            var text = result.ToJsonString();
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "ARAData", DataType = AihJson.AraData, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text }
            });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-ARA-V1.1: " + ex.Message)); }
    }

    // The risk and, where there is one, the anomaly (the taxonomy's names).
    public static (string Risk, string? Anomaly) Assess(JsonObject? healthData)
    {
        var samples = Samples(healthData, out var rate);
        var beats = Beats(samples, rate);
        if (beats.Count < 3) return ("Blue", null);                                     // too little to judge

        var intervals = beats.Zip(beats.Skip(1), (a, b) => (b - a) / (double)rate).ToList();
        var sorted = intervals.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];
        var heartRate = 60.0 / median;
        var irregular = intervals.Any(x => Math.Abs(x - median) > 0.3 * median);

        if (heartRate < 40 || heartRate > 150) return ("Red", "Medical Condition Anomaly");
        if (heartRate < 50 || heartRate > 120) return ("Orange", "Medical Condition Anomaly");
        if (irregular) return ("Yellow", "Collective Anomaly");
        if (heartRate < 60 || heartRate > 100) return ("Yellow", "Contextual Anomaly");
        return ("Green", null);
    }

    private static short[] Samples(JsonObject? healthData, out int rate)
    {
        rate = FindNumber(healthData, "SamplingFrequency") is { } f && f > 0 ? (int)f : 250;
        var first = (healthData?["HealthDataData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d));
        if (first is null) return [];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(first); } catch (FormatException) { return []; }
        var n = bytes.Length / 2;
        var s = new short[n];
        for (var i = 0; i < n; i++) s[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
        return s;
    }

    private static double? FindNumber(JsonNode? node, string name) => node switch
    {
        JsonObject o => o.Select(p => p.Key == name && p.Value is JsonValue v && v.TryGetValue<double>(out var d) ? d : FindNumber(p.Value, name)).FirstOrDefault(d => d is not null),
        JsonArray a => a.Select(x => FindNumber(x, name)).FirstOrDefault(d => d is not null),
        _ => null
    };

    // The positions of the beats: the local peaks above 60% of the strongest deviation from the mean, at least 250 ms apart.
    private static List<int> Beats(short[] samples, int rate)
    {
        var beats = new List<int>();
        if (samples.Length < rate) return beats;                                       // under a second: nothing to judge
        var mean = samples.Average(x => (double)x);
        var peak = samples.Max(x => Math.Abs(x - mean));
        if (peak < 1) return beats;
        var threshold = 0.6 * peak;
        var gap = (int)(0.25 * rate);
        for (var i = 1; i < samples.Length - 1; i++)
        {
            var v = samples[i] - mean;
            if (v < threshold || v < samples[i - 1] - mean || v < samples[i + 1] - mean) continue;
            if (beats.Count > 0 && i - beats[^1] < gap) continue;
            beats.Add(i);
        }
        return beats;
    }
}
