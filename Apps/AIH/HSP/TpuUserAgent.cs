using System;
using System.Text.Json.Nodes;

using Mpai.Aif.Api;

namespace Mpai.Aih.Hsp;

// THE USER AGENT OF A THIRD-PARTY USER. It runs TPU.orch, over the Health Front End Module (a Third-Party User runs a Health Front End: its User Profile makes the Module do what a
// Third-Party User does). It registers with the Health Back End and asks for AIH Data to be processed as the licence terms allow, and takes the answer or the refusal. It deals with its
// Controller only; the request and the answer travel between the Controllers. It makes no AIH Data and asks for nothing else.
public sealed class TpuUserAgent : IDisposable
{
    public const string Module = HfeUserAgent.Module;

    private readonly ModuleSession s;
    private JsonObject? tokens;

    public string UserId { get; }

    public TpuUserAgent(IControllerApi api, string userId, string workflowFile, string vault, (string Service, string Anchor, string Rendezvous, string KnownAs)? service = null)
    {
        UserId = userId;
        s = new ModuleSession(api, workflowFile, vault, service,
            ("RegisterIn", "AIH-REG-V1.1"), ("RequestIn", "AIH-AHP-V1.1"), ("RegisterAnswerOut", ""), ("ResponseOut", ""));
        s.Start();
    }

    public void Dispose() => s.Dispose();

    // The Tokens the Back End gave (null before the registration).
    public JsonObject? Tokens => tokens;

    // Registers with the Back End (a Third-Party User Profile, or a User Profile that states a Membership). Returns the Back End's answer.
    public JsonObject Register(JsonObject request)
    {
        s.Drain();
        s.Send("RegisterIn", request.ToJsonString());
        var answer = AihJson.Parse(s.Receive("RegisterAnswerOut", 60_000) ?? throw new TimeoutException("The Back End gave no answer to the registration."), "Register");
        tokens = AihJson.RegisterResponseOf(answer)?["Tokens"] as JsonObject;
        return answer;
    }

    // Asks for an AIH Data to be processed as its licence prescribes: the answer holds the AIH Data and what was found (ProcessingStatus true), or is a refusal (false).
    public JsonObject Process(string aihDataId) => Ask("AIHDataProcessing", aihDataId);

    // A request for a service a Third-Party User has no Token for (de-identification, audit, storage): its Health Front End refuses it.
    public JsonObject Ask(string processingType, string aihDataId)
    {
        s.Drain();
        s.Send("RequestIn", new JsonObject
        {
            ["Header"] = AihJson.Process, ["UserID"] = UserId, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject
            {
                ["Request"] = new JsonObject
                {
                    ["AIHDataOrAIHDataID"] = new JsonArray(JsonValue.Create(aihDataId)), ["ProcessingType"] = processingType,
                    ["Tokens"] = (tokens ?? AihJson.TokensFor(UserId, [processingType])).DeepClone()
                }
            })
        }.ToJsonString());
        return AihJson.Parse(s.Receive("ResponseOut", 60_000) ?? throw new TimeoutException("The Back End gave no answer to the request."), "AIH Data Process");
    }
}
