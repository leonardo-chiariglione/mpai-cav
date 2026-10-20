using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Core;

using Mpai.Ua;
namespace Mpai.Rca;

// THE DEVICES OF A MEETING (the Virtual Meeting Secretary App, Apps/VMS): what the
// workflow binds to the boundary of PAF-VMS, which runs continuously. A client
// supplies what only it can do - hear the microphone, read the text box, show the
// minutes, play the avatar - and these turn it into, and out of, the Data Types of
// the Module.

// One utterance of a participant: who, and what - typed (Text) or spoken (Speech,
// an OSD-BSO-V1.5 as the client captured it) - and, where the client knows it, how
// (an MMC-EPS-V2.5).
public sealed record MeetingUtterance(string Participant, string? Text = null, string? SpeechJson = null, string? PersonalStatusJson = null);

// THE PARTICIPANT: each utterance, as the participant's Portable Avatar (PAF-PAV-V1.6)
// - the participant's name as the Avatar's ID, the Text or the Speech Object, the
// Personal Status - the form Portable Avatar Demultiplexing takes apart.
public sealed class ParticipantDevice(Func<CancellationToken, Task<MeetingUtterance?>> next) : IDevice
{
    public Task DeliverAsync(string dataType, string json, CancellationToken cancel) => Task.CompletedTask;

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            MeetingUtterance? said;
            try { said = await next(cancel); }
            catch (OperationCanceledException) { yield break; }
            if (said is null) yield break;                       // the participant has left
            yield return ("PAF-PAV-V1.6", PortableAvatarOf(said));
        }
    }

    public Task SafeStopAsync() => Task.CompletedTask;

    public static string PortableAvatarOf(MeetingUtterance said)
    {
        var pav = new JsonObject
        {
            ["Header"] = "PAF-PAV-V1.6",
            ["PortableAvatarID"] = Guid.NewGuid().ToString(),
            ["AvatarOrAvatarID"] = new JsonArray(said.Participant)
        };
        if (!string.IsNullOrWhiteSpace(said.Text))
            pav["TextObject"] = JsonNode.Parse(MpaiJson.ToJson(BasicTextObject.FromText(said.Text)));
        if (said.SpeechJson is not null)
            pav["SPOOrSPOID"] = new JsonArray(JsonNode.Parse(said.SpeechJson));
        if (said.PersonalStatusJson is not null)
            pav["EntityState"] = Mpai.Core.OSD.EntityStates.OfPersonalStatus(said.PersonalStatusJson);
        return pav.ToJsonString();
    }
}

// THE MINUTES: the Summary (MMC-SUM-V2.5) the Module gives after each utterance, as
// its text, to whatever shows it.
public sealed class MinutesDevice(Func<string, Task> show) : IDevice
{
    public Task DeliverAsync(string dataType, string json, CancellationToken cancel)
    {
        var data = JsonNode.Parse(json)?["SummaryData"] as JsonArray;
        var text = data?.Select(d => (string?)d?["Data"]).FirstOrDefault(t => t is not null) ?? "";
        return show(text);
    }

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => { });
        yield break;
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}

// THE SECRETARY: its Portable Avatar (PAF-PAV-V1.6), given to whatever plays it - the
// client turns it into an utterance of its avatar (AvatarUtterance.FromPortableAvatar).
public sealed class SecretaryDevice(Func<string, Task> play, string id = "Secretary") : IDevice
{
    public Task DeliverAsync(string dataType, string json, CancellationToken cancel) => play(json);

    // The avatar's own ID is the device's to give, once, as the meeting begins: the Portable Avatars the Module makes carry it.
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        yield return ("string", System.Text.Json.JsonSerializer.Serialize(id));
        await Task.Delay(Timeout.Infinite, cancel).ContinueWith(_ => { });
    }

    public Task SafeStopAsync() => Task.CompletedTask;
}
