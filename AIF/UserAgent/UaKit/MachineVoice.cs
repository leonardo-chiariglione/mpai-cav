using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance

namespace Mpai.UaKit;

// THE VOICE OF THE SCREEN AND OF THE AVATAR DEVICES. A prompt of the workflow, or an instruction of an acquiring device, is shown and
// said: the words go to the Module that makes machine speech and face from text (PAF-RSR) through a Controller of their own - this User
// Agent asks the Controller and no more - and the Portable Avatar that comes back is presented on the avatar the window keeps. The same is
// done for the words of a Portable Avatar that carries text and no speech. A line that cannot be spoken is recorded; it does not stop the App.
// The App gives the Controller (its provider has the leaf AIMs of PAF-RSR) and where a failure is recorded.
public sealed class MachineVoice : IDisposable
{
    private const string RsrModule = "1PAF-RSR-V1.6-I01";
    private const string BTO = "OSD-BTO-V1.5";
    private const string PAV = "PAF-PAV-V1.6";

    private readonly AvatarUaHost _avatar;
    private readonly string _machineName;
    private readonly Func<ControllerApi> _controller;
    private readonly Action<string, Exception> _record;
    private ControllerApi? _api;
    private bool _started;

    public MachineVoice(AvatarUaHost avatar, string machineName, Func<ControllerApi> controller, Action<string, Exception> record)
    { _avatar = avatar; _machineName = machineName; _controller = controller; _record = record; }

    public async Task SayAsync(string words)
    {
        try
        {
            _api ??= _controller();
            if (!_started)
            {
                var s = await Task.Run(() => _api!.StartFlow(RsrModule));
                if (s != AifError.OK) { _record("say", new Exception("could not start " + RsrModule + ": " + s)); return; }
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
            if (!r.Ok) { _record("say", new Exception(r.Error.ToString())); return; }
            if (AvatarUtterance.FromPortableAvatar(r.ByType(PAV)) is not { } utterance) return;
            await PresentAsync(utterance);
        }
        catch (Exception ex) { _record("say", ex); }
    }

    // THE MACHINE'S VOICE SAYING WORDS, AS A WAV, and not presented: for a person who types what they would say - the words are spoken in her voice and given to the Module as
    // speech. Null when it cannot be made (recorded, as a line that cannot be spoken is).
    public async Task<byte[]?> SynthesizeAsync(string words)
    {
        try
        {
            _api ??= _controller();
            if (!_started)
            {
                var s = await Task.Run(() => _api!.StartFlow(RsrModule));
                if (s != AifError.OK) { _record("synthesize", new Exception("could not start " + RsrModule + ": " + s)); return null; }
                _started = true;
            }
            var text = MpaiJson.ToJson(BasicTextObject.FromText(words));
            var inputs = new List<ControllerApi.Datum>
            {
                new(BTO, 1, text), new(BTO, 2, text),
                new("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
                new("string", 1, System.Text.Json.JsonSerializer.Serialize(_machineName))
            };
            var r = await Task.Run(() => _api!.Advance(RsrModule, inputs));
            if (!r.Ok) { _record("synthesize", new Exception(r.Error.ToString())); return null; }
            return AvatarUtterance.FromPortableAvatar(r.ByType(PAV))?.MachineSpeechWav;
        }
        catch (Exception ex) { _record("synthesize", ex); return null; }
    }

    // An utterance, presented on the avatar; the step ends when she has finished speaking.
    public Task PresentAsync(AvatarUtterance utterance) => StandardDevices.PresentAsync(_avatar, utterance);

    public void Dispose()
    {
        try { if (_started) _api?.StopFlow(RsrModule); } catch { }
        try { _api?.Dispose(); } catch { }
    }
}
