using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Dia;

// AIH-DIA-V1.1 - De-Identification and Anonymisation: "de-identifies or anonymises AIH Data". A request (which AIH Data, what to do, by which technique of the
// taxonomy) and the AIH Data (given by Storage) in; a response and the new AIH Data (kept by Storage, linked to the original by ParentAIHDataID) out.
//
// The request waits for the AIH Data, and the AIH Data that nobody asked for passes unnoticed (Storage gives its AIH Data to every AIM that takes it).
//   De-identification (Removal of Identifiers, Hashing, Tokenisation, Data Masking): the End User's identifier is replaced by a pseudonym (a keyed hash, so that
//     the same End User has the same pseudonym), and the Model Licence by its identifier; the state is De-Identified.
//   Anonymisation (Perturbation, K-Anonymity, Generalisation, Data Aggregation, L-Diversity, Differential Privacy, Synthetic Data Generation): the above, and
//     the samples of the Health Data are perturbed (noise of 2% of the signal's peak, from a generator seeded by the AIH Data's identifier); the state is Anonymised.
//   Homomorphic Encryption is not done here: the response says so (ProcessingState false).
public sealed class DiaAimProcessor : IAimProcessor
{
    private static readonly string[] DeIdentify = ["Removal of Identifiers", "Hashing", "Tokenisation", "Data Masking"];
    private static readonly string[] Anonymise = ["Perturbation", "K-Anonymity", "Generalisation", "Data Aggregation", "L-Diversity", "Differential Privacy", "Synthetic Data Generation"];

    private readonly string _requestIn, _dataIn, _responseOut, _dataOut;
    private readonly byte[] _key;
    private readonly Dictionary<string, JsonObject> _waiting = new();

    public string InstanceId { get; }

    public DiaAimProcessor(string instanceId, AimPortReader ports, string pseudonymKey)
    {
        InstanceId = instanceId;
        _key = Encoding.UTF8.GetBytes(pseudonymKey);
        _requestIn   = ports.Input(AihJson.DeId);
        _dataIn      = ports.Input(AihJson.AihData);
        _responseOut = ports.Output(AihJson.DeId);
        _dataOut     = ports.Output(AihJson.AihData);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            var outputs = new Dictionary<string, string>();

            if (message.Ports.TryGetValue(_requestIn, out var requestJson) && !string.IsNullOrWhiteSpace(requestJson))
            {
                var request = AihJson.Parse(requestJson, "De-Identification and Anonymisation");
                var asked = (request["DeIDAndAnonymData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Request"] as JsonObject).FirstOrDefault(r => r is not null)
                            ?? throw new FormatException("the request holds no Request.");
                var id = asked["AIHDataID"]?.GetValue<string>() ?? throw new FormatException("the request names no AIH Data.");
                _waiting[id] = asked;
            }

            if (message.Ports.TryGetValue(_dataIn, out var dataJson) && !string.IsNullOrWhiteSpace(dataJson))
            {
                var data = AihJson.Parse(dataJson, "AIH Data");
                var id = data["AIHDataID"]?.GetValue<string>() ?? "";
                if (_waiting.Remove(id, out var asked))
                {
                    var made = Make(data, asked);
                    if (made is not null) outputs[_dataOut] = made.ToJsonString();
                    outputs[_responseOut] = Response(data["UserID"]?.GetValue<string>() ?? "", id, made?["AIHDataID"]?.GetValue<string>(), made is not null).ToJsonString();
                }
            }

            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "DeIDAndAnonym", DataType = AihJson.DeId, Payload = outputs.Values.FirstOrDefault() ?? "", Ports = outputs });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-DIA-V1.1: " + ex.Message)); }
    }

    // The response says which AIH Data was processed and which AIH Data resulted (the copy), so that the requester need not know how the copy is named.
    public static JsonObject Response(string user, string id, string? newId, bool done)
    {
        var response = new JsonObject { ["AIHDataID"] = id, ["ProcessingState"] = done };
        if (newId is not null) response["NewAIHDataID"] = newId;
        return new()
        {
            ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = user,
            ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Response"] = response })
        };
    }

    // The new AIH Data, or null where the technique cannot make it.
    private JsonObject? Make(JsonObject original, JsonObject asked)
    {
        var type = asked["DeIDAndAnonymType"]?.GetValue<string>() ?? "De-Identify";
        var algorithm = asked["DeIDAndAnonymAlgoID"]?.GetValue<string>() ?? "";
        var deIdentify = DeIdentify.Contains(algorithm);
        var anonymise = Anonymise.Contains(algorithm);
        var wantsAnonymised = type is "Anonymise" or "De-Identify and Anonymise";
        if (!(deIdentify || anonymise) || (wantsAnonymised && !anonymise) || (!wantsAnonymised && !deIdentify)) return null;

        var copy = (JsonObject)original.DeepClone();
        var id = original["AIHDataID"]!.GetValue<string>();
        var user = original["UserID"]?.GetValue<string>() ?? "";
        var pseudonym = "pseud-" + Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(user)))[..12].ToLowerInvariant();

        copy["UserID"] = pseudonym;
        copy["AIHDataID"] = id + (anonymise ? "-anon" : "-deid");
        copy["AIHDataTime"] = AihJson.Time();
        var content = (JsonObject)copy["AIHData"]!;
        content["ParentAIHDataID"] = id;
        content["DeIDAndAnonym"] = anonymise ? "Anonymised" : "De-Identified";
        if (content["ModelLicence"] is JsonObject licence) content["ModelLicence"] = licence["ModelLicenceID"]?.GetValue<string>() ?? "";
        content.Remove("ARAData");                                                      // it is of the original
        if (content["HealthData"] is JsonObject health)
        {
            health["EndUserID"] = pseudonym;
            if (anonymise) Perturb(health, id);
        }
        return copy;
    }

    // Noise of 2% of the peak on 16-bit samples, from a generator seeded by the identifier (the same AIH Data gives the same result).
    private static void Perturb(JsonObject health, string seed)
    {
        if (health["HealthDataData"] is not JsonArray items) return;
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0));
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["Data"]?.GetValue<string>() is not { Length: > 0 } text) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(text); } catch (FormatException) { continue; }
            var n = bytes.Length / 2;
            var samples = new short[n];
            for (var i = 0; i < n; i++) samples[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            var amplitude = Math.Max(1, samples.Length == 0 ? 1 : samples.Max(s => Math.Abs((int)s))) * 0.02;
            for (var i = 0; i < n; i++)
            {
                var v = Math.Clamp(samples[i] + (random.NextDouble() * 2 - 1) * amplitude, short.MinValue, short.MaxValue);
                var s = (short)Math.Round(v);
                bytes[2 * i] = (byte)(s & 0xFF); bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
            }
            item["Data"] = Convert.ToBase64String(bytes);
        }
    }
}
