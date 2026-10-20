using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Aih.Mdl;

// AIH-MDL-V1.1 - Model Licensing. "This AIM transforms Health Data into AIH Data by adding a Model Licence": Health Data and Model Licence in, AIH Data out.
//
// The licence travels with the data. It names the End User and the AIH Data it applies to; the AIH Data made takes that identifier, so that the Back End,
// which finds the Smart Contract's identifier for it, and the audit, which finds the operations on it, speak of the same thing. The Smart Contract's identifier
// and the state of de-identification are not here: the Back End sets them later (they are optional in AIH Data).
public sealed class MdlAimProcessor : IAimProcessor
{
    private readonly string _health, _licence, _out;

    public string InstanceId { get; }

    public MdlAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId = instanceId;
        _health  = ports.Input(AihJson.HealthData);
        _licence = ports.Input(AihJson.ModelLicence);
        _out     = ports.Output(AihJson.AihData);
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_health, out var healthJson) || string.IsNullOrWhiteSpace(healthJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-MDL-V1.1: no Health Data on input port '{_health}'."));
        if (!message.Ports.TryGetValue(_licence, out var licenceJson) || string.IsNullOrWhiteSpace(licenceJson))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-MDL-V1.1: no Model Licence on input port '{_licence}'."));
        try
        {
            var health  = AihJson.Parse(healthJson, "Health Data");
            var licence = AihJson.Parse(licenceJson, "Model Licence");
            var user    = health["EndUserID"]?.GetValue<string>() ?? throw new FormatException("the Health Data names no End User.");
            if (licence["EndUserID"]?.GetValue<string>() is { } licenceUser && licenceUser != user)
                return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"AIH-MDL-V1.1: the Model Licence is of End User '{licenceUser}', the Health Data of '{user}'."));
            var id = licence["AIHDataID"]?.GetValue<string>() ?? AihJson.NewId("ahd");

            var data = new JsonObject
            {
                ["Header"] = AihJson.AihData, ["UserID"] = user, ["AIHDataID"] = id, ["AIHDataTime"] = AihJson.Time(),
                ["AIHData"] = new JsonObject { ["HealthData"] = health.DeepClone(), ["ModelLicence"] = licence.DeepClone() }
            };
            var text = data.ToJsonString();
            return Task.FromResult(new Message
            {
                MessageId = message.MessageId, MessageType = "AIHData", DataType = AihJson.AihData, Payload = text,
                Ports = new Dictionary<string, string> { [_out] = text }
            });
        }
        catch (Exception ex) { return Task.FromResult(Message.Error(message.MessageId, InstanceId, "AIH-MDL-V1.1: " + ex.Message)); }
    }
}
