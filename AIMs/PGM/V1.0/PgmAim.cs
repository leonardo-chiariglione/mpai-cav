using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Pgm;

// The base of the PGM-AUA AIMs of the first slice. An AIM here is an AIM (a composite is no different from a leaf): it reads and writes only the Ports its Metadata
// declares, found by Data Type and PortNumber through AimPortReader, and it handles Data as JSON objects of the Data Types of those Ports - no private format.
// The keys of the Ports an AIM asked for are kept, so that a test can compare them with the Ports of the Metadata.
public abstract class PgmAim : IAimProcessor
{
    private readonly AimPortReader _ports;
    private readonly Dictionary<string, string> _inputs = new(), _outputs = new();

    protected PgmAim(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _ports = ports;
    }

    public string InstanceId { get; }

    // The key of a Port, for whoever builds a Message for this AIM (a composite AIM that calls its Sub-AIMs, a test).
    public string InputKey(string dataType, int portNumber = 1) => _ports.Input(dataType, portNumber);
    public string OutputKey(string dataType, int portNumber = 1) => _ports.Output(dataType, portNumber);

    // Port key -> Data Type, for the Ports the AIM uses: what a test compares with the Metadata.
    public IReadOnlyDictionary<string, string> InputPorts => _inputs;
    public IReadOnlyDictionary<string, string> OutputPorts => _outputs;

    protected string In(string dataType, int portNumber = 1)
    {
        var key = _ports.Input(dataType, portNumber);
        _inputs[key] = dataType;
        return key;
    }

    protected string Out(string dataType, int portNumber = 1)
    {
        var key = _ports.Output(dataType, portNumber);
        _outputs[key] = dataType;
        return key;
    }

    public abstract Task<Message> ProcessAsync(Message message);

    // What is pending on a Port of this Message, as a JSON object.
    protected static JsonObject? Read(Message message, string key) =>
        message.Ports.TryGetValue(key, out var json) && !string.IsNullOrWhiteSpace(json) ? JsonNode.Parse(json) as JsonObject : null;

    protected Message Give(Message message, IEnumerable<(string Key, JsonNode? Value)> outputs)
    {
        var ports = new Dictionary<string, string>();
        foreach (var (key, value) in outputs)
            if (value is not null) ports[key] = value.ToJsonString();
        return new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = ports };
    }

    protected static Task<Message> Done(Message m) => Task.FromResult(m);

    // ---- Data that every PGM Data Type carries ----------------------------------------------------------------------------------------------------

    public static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // OSD-STM-V1.5, a single instant.
    public static JsonObject Time()
    {
        var t = Now();
        return new JsonObject
        {
            ["Header"] = "OSD-STM-V1.5",
            ["SimpleTimeID"] = NewId("t"),
            ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 1, ["StartTime"] = t, ["EndTime"] = t, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0 })
        };
    }

    // A text object (OSD-BTO-V1.5).
    public static JsonObject Text(string text) => new()
    {
        ["Header"] = "OSD-BTO-V1.5",
        ["BasicTextObjectID"] = NewId("txt"),
        ["BasicTextData"] = new JsonArray(new JsonObject { ["Data"] = text })
    };

    public static string? TextOf(JsonObject? bto) =>
        bto?["BasicTextData"] is JsonArray a ? string.Concat(a.OfType<JsonObject>().Select(o => (string?)o["Data"])) : null;

    // The identity of the session, taken from a Data that has it.
    public readonly record struct Session(string MInstance, string UEnvironment, string SessionId)
    {
        public static Session Of(JsonObject? o) =>
            new((string?)o?["MInstance"] ?? "m-instance", (string?)o?["UEnvironment"] ?? "u-environment", (string?)o?["SessionID"] ?? "session");

        public void Stamp(JsonObject o)
        {
            o["MInstance"] = MInstance;
            o["UEnvironment"] = UEnvironment;
            o["SessionID"] = SessionId;
        }
    }
}
