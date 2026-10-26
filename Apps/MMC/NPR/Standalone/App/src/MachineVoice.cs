using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance
using Mpai.Providers;

namespace MmcNpr;

// THE VOICE OF THE SCREEN AND OF THE AVATAR DEVICES. A prompt of the workflow is shown and said: the words go to the Module
// that makes machine speech and face from text (PAF-RSR) through a Controller of their own - this User Agent asks the Controller
// and no more - and the Portable Avatar that comes back is presented on the avatar the window keeps. The same is done for the
// words of a Portable Avatar that carries text and no speech. A line that cannot be spoken is recorded; it does not stop the App.
internal sealed class MachineVoice : IDisposable
{
    private const string RsrModule = "1PAF-RSR-V1.6-I01";
    private const string BTO = "OSD-BTO-V1.5";
    private const string PAV = "PAF-PAV-V1.6";

    private readonly AvatarUaHost _avatar;
    private readonly string _machineName;
    private ControllerApi? _api;
    private bool _started;

    public MachineVoice(AvatarUaHost avatar, string machineName)
    { _avatar = avatar; _machineName = machineName; }

    public async Task SayAsync(string words)
    {
        try
        {
            _api ??= new ControllerApi(Mpai.Core.MpaiPaths.Amds, Mpai.Core.MpaiPaths.Settings, store => new AmqProvider(store));
            if (!_started)
            {
                var s = await Task.Run(() => _api!.StartFlow(RsrModule));
                if (s != AifError.OK) { Program.Record("say", new Exception("could not start " + RsrModule + ": " + s)); return; }
                _started = true;
            }
            // PAF-RSR takes the Text Object twice (#1 to Text-To-Speech, #2 to Generative Face Description), the Avatar Model and the machine's ID.
            var text = MpaiJson.ToJson(BasicTextObject.FromText(words));
            var inputs = new List<ControllerApi.Datum>
            {
                new(BTO, 1, text),
                new(BTO, 2, text),
                new("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
                new("string", 1, System.Text.Json.JsonSerializer.Serialize(_machineName))
            };
            var r = await Task.Run(() => _api!.Advance(RsrModule, inputs));
            if (!r.Ok) { Program.Record("say", new Exception(r.Error.ToString())); return; }
            if (AvatarUtterance.FromPortableAvatar(r.ByType(PAV)) is not { } utterance) return;
            await PresentAsync(utterance);
        }
        catch (Exception ex) { Program.Record("say", ex); }
    }

    // An utterance, presented on the avatar; the step ends when she has finished speaking.
    public async Task PresentAsync(AvatarUtterance utterance)
    {
        await _avatar.PresentAsync(utterance);
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
    }

    public void Dispose()
    {
        try { if (_started) _api?.StopFlow(RsrModule); } catch { }
        try { _api?.Dispose(); } catch { }
    }
}
