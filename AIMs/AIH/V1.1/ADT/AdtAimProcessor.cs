using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Adt;

// AIH-ADT-V1.1 - Auditing: "checks whether unlicensed operations were performed on AIH Data" - so that the End User can monitor the use of their AIH Data.
//
// WHAT THE OPERATIONS ARE. Every operation that makes AIH Data from other AIH Data (the de-identified or anonymised copy) leaves the new AIH Data linked to the one
// it came from (ParentAIHDataID). Auditing sees the AIH Data that Storage gives it (Storage gives its AIH Data to every AIM that takes it) and keeps the derived
// ones; asked to audit, it asks the Blockchain - through Licence Confirm, one at a time, since a message carries one request - whether the operation that made
// each was licensed, and answers with those that were not: the AIH Data, and when the operation was done (the Time of the AIH Data). The operation is read from
// the state the AIH Data states (De-Identified or Anonymised: DeIDAndAnonym), and the one who did it is the Back End (the setting BackEndId).
public sealed class AdtAimProcessor : IAimProcessor
{
    private readonly string _requestIn, _dataIn, _confirmIn, _responseOut, _confirmOut;
    private readonly string _backEnd;
    private readonly Dictionary<string, JsonObject> _derived = new();
    private readonly Dictionary<string, string> _owners = new();                  // the End User of each AIH Data seen that is not derived
    private Audit? _running;

    private sealed class Audit(string user, string id)
    {
        public string User { get; } = user;
        public string AuditId { get; } = id;
        public Queue<JsonObject> ToAsk { get; } = new();
        public (string ConfirmId, JsonObject Data)? Current { get; set; }
        public List<JsonObject> Unlicensed { get; } = new();
    }

    public string InstanceId { get; }

    public AdtAimProcessor(string instanceId, AimPortReader ports, string backEndId)
    {
        InstanceId = instanceId;
        _backEnd = backEndId;
        _requestIn   = ports.Input(AihJson.Audit);
        _dataIn      = ports.Input(AihJson.AihData);
        _confirmIn   = ports.Input(AihJson.LicenceConfirm);
        _responseOut = ports.Output(AihJson.Audit);
        _confirmOut  = ports.Output(AihJson.LicenceConfirm);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        try
        {
            var outputs = new Dictionary<string, string>();

            // AIH Data seen: the derived is kept.
            if (message.Ports.TryGetValue(_dataIn, out var dataJson) && !string.IsNullOrWhiteSpace(dataJson))
            {
                var data = AihJson.Parse(dataJson, "AIH Data");
                if (data["AIHDataID"]?.GetValue<string>() is { } id)
                {
                    if (data["AIHData"]?["ParentAIHDataID"] is not null) _derived[id] = data;
                    else if (data["UserID"]?.GetValue<string>() is { } owner) _owners[id] = owner;
                }
            }

            // An audit asked for.
            if (message.Ports.TryGetValue(_requestIn, out var requestJson) && !string.IsNullOrWhiteSpace(requestJson))
            {
                var request = AihJson.Parse(requestJson, "Audit");
                var user = request["UserID"]?.GetValue<string>() ?? "";
                var asked = request["Audit"]?["Request"] as JsonObject ?? throw new FormatException("the audit holds no Request.");
                _running = new Audit(user, request["AuditID"]?.GetValue<string>() ?? AihJson.NewId("adt"));
                var from = Moment(asked["AuditPeriod"]);
                foreach (var data in _derived.Values.Where(d => Owner(d) == user && Moment(d["AIHDataTime"]) >= from).OrderBy(d => Moment(d["AIHDataTime"])))
                    _running.ToAsk.Enqueue(data);
                Next(outputs);
            }

            // A licence confirmed or not.
            if (message.Ports.TryGetValue(_confirmIn, out var confirmJson) && !string.IsNullOrWhiteSpace(confirmJson) && _running is { Current: not null } running)
            {
                var confirm = AihJson.Parse(confirmJson, "Licence Confirm");
                var status = (confirm["LicenceConfirmData"] as JsonArray)?.OfType<JsonObject>().Select(o => o["Response"]?["Status"]?.GetValue<bool>()).FirstOrDefault(s => s is not null);
                if (confirm["LicenceConfirmID"]?.GetValue<string>() == running.Current.Value.ConfirmId)
                {
                    if (status != true) running.Unlicensed.Add(running.Current.Value.Data);
                    running.Current = null;
                    Next(outputs);
                }
            }

            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "Audit", DataType = AihJson.Audit, Payload = outputs.Values.FirstOrDefault() ?? "", Ports = outputs });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-ADT-V1.1: " + ex.Message)); }
    }

    // The next operation to ask about, or the answer when there is none.
    private void Next(Dictionary<string, string> outputs)
    {
        if (_running is null) return;
        if (_running.ToAsk.Count > 0)
        {
            var data = _running.ToAsk.Dequeue();
            var parent = data["AIHData"]!["ParentAIHDataID"]!.GetValue<string>();
            var confirm = AihJson.LicenceConfirmRequest(Owner(data), parent, "DeIDAndAnonym", _backEnd);
            _running.Current = (confirm["LicenceConfirmID"]!.GetValue<string>(), data);
            outputs[_confirmOut] = confirm.ToJsonString();
            return;
        }
        outputs[_responseOut] = Answer(_running).ToJsonString();
        _running = null;
    }

    // The End User the derived AIH Data came from: the one of the AIH Data it was made from, if Auditing has seen that (a copy holds a pseudonym only).
    private string Owner(JsonObject data) => _owners.GetValueOrDefault(data["AIHData"]?["ParentAIHDataID"]?.GetValue<string>() ?? "", "");

    private static DateTimeOffset Moment(JsonNode? time) =>
        DateTimeOffset.TryParse(time?["Data"]?.GetValue<string>(), out var at) ? at : DateTimeOffset.MinValue;

    private static JsonObject Answer(Audit audit) => new()
    {
        ["Header"] = AihJson.Audit, ["UserID"] = audit.User, ["AuditID"] = audit.AuditId, ["AuditTime"] = AihJson.Time(),
        ["Audit"] = new JsonObject
        {
            ["Response"] = new JsonObject
            {
                ["AIHDataIDs"] = new JsonArray(audit.Unlicensed.Select(d => (JsonNode)new JsonObject
                {
                    ["AIHDataID"] = d["AIHDataID"]!.GetValue<string>(), ["ProcessTime"] = d["AIHDataTime"]!.DeepClone()
                }).ToArray())
            }
        }
    };
}
