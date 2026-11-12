using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Onnx;

namespace Mpai.Nnw.NoConstraint;

// Post-Training Watermark Insertion (NNW-PWI-V2.0) with the NoConstraint method. The model (AIP-MLO-V1.0) is given as an ML Model Object whose MLModelData items hold
// files in base64; every item that is an ONNX file is marked (an item that is not, such as the weights of a final layer, is left as it is). The Payload (NNW-PLD) is a
// string of bits; the Key (NNW-KEY) a sequence of reals, from which the positions and the values come. A Locator (NNW-WLC), where given, must name this method and may name
// the tensors (Targets). The watermarked model is a new ML Model Object, the same in all else.
public sealed class PwiAimProcessor : IAimProcessor
{
    private readonly string _model, _payload, _key, _locator, _out;
    public string InstanceId { get; }

    public PwiAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _model = ports.Input(NnwJson.Model); _payload = ports.Input(NnwJson.Payload); _key = ports.Input(NnwJson.Key);
        _locator = ports.InputOrDefault(NnwJson.Locator, "Locator"); _out = ports.Output(NnwJson.Model);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            var model = NnwJson.Required(message, _model, "UnwatermarkedModel");
            var payload = NoConstraint.Bits(NnwJson.Required(message, _payload, "Payload")["PayloadData"]?.GetValue<string>() ?? "");
            var seed = NnwJson.Seed(NnwJson.Required(message, _key, "Key"));
            var targets = NnwJson.Targets(message, _locator, out var method);
            if (method is not null && !method.StartsWith(NoConstraint.MethodPrefix, StringComparison.Ordinal))
                return Fail(message, $"the Locator names the method '{method}', which this Implementation does not implement.");
            var valuesPerBit = NoConstraint.ValuesPerBitOf(method) ?? NoConstraint.ValuesPerBit;
            var marked = model.DeepClone().AsObject();
            var items = (marked["MLModelData"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            var done = 0;
            foreach (var item in items)
            {
                if (item["Data"]?.GetValue<string>() is not { Length: > 0 } b64) continue;
                byte[] bytes; try { bytes = Convert.FromBase64String(b64); } catch (FormatException) { continue; }
                if (!OnnxFloats.IsOnnx(bytes)) continue;
                item["Data"] = Convert.ToBase64String(NoConstraint.InsertOnnx(bytes, seed, payload, targets, valuesPerBit));
                done++;
            }
            if (done == 0) return Fail(message, "the model carries no ONNX file with float tensors: a format this Implementation does not support.");
            marked["MLModelObjectID"] = (model["MLModelObjectID"]?.GetValue<string>() ?? "model") + "-wm";
            marked["DescrMetadata"] = $"{model["DescrMetadata"]?.GetValue<string>()} - watermarked: {NoConstraint.MethodName(payload.Length, valuesPerBit)}".TrimStart(' ', '-');
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "WatermarkedModel", DataType = NnwJson.Model, Payload = marked.ToJsonString(), Ports = new() { [_out] = marked.ToJsonString() } });
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return Fail(message, e.Message); }
    }

    private Task<Message> Fail(Message m, string why) => Task.FromResult(Message.Error(m.MessageId, InstanceId, "NNW-PWI-V2.0: " + why));
}

// Watermark Extraction (NNW-WEX-V2.0) with the NoConstraint method: the Payload read from the model (the first ONNX file of its MLModelData). The number of bits is that of
// the method named by the Locator, which is therefore needed. The DescrMetadata of the Retrieved Payload says the correlation, the threshold, and whether the mark is present;
// where it is not, the bits are those of chance.
public sealed class WexAimProcessor : IAimProcessor
{
    private readonly string _model, _key, _locator, _out;
    public string InstanceId { get; }

    public WexAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _model = ports.Input(NnwJson.Model); _key = ports.Input(NnwJson.Key);
        _locator = ports.InputOrDefault(NnwJson.Locator, "Locator"); _out = ports.Output(NnwJson.Payload);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            var model = NnwJson.Required(message, _model, "ModifiedModel");
            var seed = NnwJson.Seed(NnwJson.Required(message, _key, "Key"));
            var targets = NnwJson.Targets(message, _locator, out var method);
            if (NoConstraint.BitsOf(method) is not int bits)
                return Fail(message, method is null ? "no Locator: it names the method and so the number of bits of the Payload." : $"the Locator names the method '{method}', which this Implementation does not implement.");
            var onnx = (model["MLModelData"] as JsonArray)?.OfType<JsonObject>().Select(i => i["Data"]?.GetValue<string>()).Where(d => !string.IsNullOrEmpty(d))
                       .Select(d => { try { return Convert.FromBase64String(d!); } catch (FormatException) { return null; } }).FirstOrDefault(b => b is not null && OnnxFloats.IsOnnx(b));
            if (onnx is null) return Fail(message, "the model carries no ONNX file with float tensors.");
            var valuesPerBit = NoConstraint.ValuesPerBitOf(method) ?? NoConstraint.ValuesPerBit;
            var found = NoConstraint.ExtractOnnx(onnx, seed, bits, targets, valuesPerBit);
            var result = new JsonObject
            {
                ["Header"] = NnwJson.Payload, ["PayloadID"] = "pld-" + Guid.NewGuid().ToString("N")[..12], ["PayloadTime"] = DateTime.UtcNow.ToString("o"),
                ["PayloadData"] = found.Payload,
                ["DescrMetadata"] = $"{(found.Present ? "mark present" : "mark NOT present")}: strength {found.Strength:F2}, threshold {found.Threshold:F2}, correlation {found.Correlation:F4} ({NoConstraint.MethodName(bits, valuesPerBit)})"
            };
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "RetrievedPayload", DataType = NnwJson.Payload, Payload = result.ToJsonString(), Ports = new() { [_out] = result.ToJsonString() } });
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return Fail(message, e.Message); }
    }

    private Task<Message> Fail(Message m, string why) => Task.FromResult(Message.Error(m.MessageId, InstanceId, "NNW-WEX-V2.0: " + why));
}

public static class NnwJson
{
    public const string Model = "AIP-MLO-V1.0", Payload = "NNW-PLD-V2.0", Key = "NNW-KEY-V2.0", Locator = "NNW-WLC-V2.0";

    public static JsonObject Required(Message m, string port, string name) =>
        m.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject o ? o : throw new FormatException($"no {name} on its input Port.");

    public static byte[] Seed(JsonObject key)
    {
        var data = (key["KeyData"] as JsonArray)?.Select(n => n!.GetValue<double>()).ToArray() ?? throw new FormatException("the Key has no KeyData.");
        if (data.Length == 0) throw new FormatException("the Key is empty.");
        return NoConstraint.Seed(data);
    }

    // The tensors a Locator names (null: the method chooses), and the method it names (null: no Locator).
    public static IReadOnlyCollection<string>? Targets(Message m, string port, out string? method)
    {
        method = null;
        if (!m.Ports.TryGetValue(port, out var json) || string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject locator) return null;
        method = locator["Method"]?.GetValue<string>();
        var names = (locator["Targets"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray();
        return names is { Length: > 0 } ? names : null;
    }

    // A Key (NNW-KEY-V2.0) of `count` reals drawn by the system's random generator.
    public static JsonObject NewKey(string id, int count = 8)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(count * 8);
        var reals = Enumerable.Range(0, count).Select(i => (double)BitConverter.ToUInt64(bytes, i * 8) / ulong.MaxValue).ToArray();
        return new JsonObject { ["Header"] = Key, ["KeyID"] = id, ["Precision"] = "Double", ["KeyData"] = new JsonArray(reals.Select(r => (JsonNode)r).ToArray()) };
    }

    public static JsonObject PayloadObject(string id, string bits) => new() { ["Header"] = Payload, ["PayloadID"] = id, ["PayloadData"] = bits };

    public static JsonObject LocatorObject(string id, int bits, string? keyReference = null, IEnumerable<string>? targets = null, int valuesPerBit = NoConstraint.ValuesPerBit)
    {
        var o = new JsonObject { ["Header"] = Locator, ["LocatorID"] = id, ["Method"] = NoConstraint.MethodName(bits, valuesPerBit) };
        if (targets is not null) o["Targets"] = new JsonArray(targets.Select(t => (JsonNode)t).ToArray());
        if (keyReference is not null) o["KeyReference"] = keyReference;
        return o;
    }
}

// Builds the two AIMs for a Controller.
public sealed class NnwProvider : IAimProvider
{
    public const string Pwi = "1NNW-PWI-V2.0-I01", Wex = "1NNW-WEX-V2.0-I01";
    private readonly AmdStore _amds;
    public NnwProvider(AmdStore amds) => _amds = amds;

    public bool CanCreate(string aimName) => aimName is Pwi or Wex;
    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(NnwProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => aimName switch
    {
        Pwi => new PwiAimProcessor(aimName, AimPortReader.Load(_amds, aimName)),
        Wex => new WexAimProcessor(aimName, AimPortReader.Load(_amds, aimName)),
        _ => throw new InvalidOperationException($"{aimName} is not an AIM of this provider.")
    };
}
