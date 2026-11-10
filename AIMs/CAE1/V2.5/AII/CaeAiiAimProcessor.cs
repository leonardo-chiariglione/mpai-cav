// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Cae.Aii;

// CAE-AII-V2.5 - Audio Instance Identification, as an AIF IAimProcessor.
//
// Receives a (generic, non-speech) Basic Audio Object and produces its Audio
// Instance Identifier (OSD-IID): what the sound IS, against the AudioSet taxonomy.
// It is the audio analogue of CVE-VII - where VII identifies a located visual
// object, AII identifies a located sound object. The audio arrives already in the
// format the model needs (mono, 16 kHz, 16-bit PCM), produced by Audio Qualifier
// Conversion (CAE-QCV); AII reads the samples directly - no decode here.
//
// Engine: YAMNet (yamnet.onnx). The highest-scoring AudioSet class becomes the
// instance identity: InstanceLabel = the class (e.g. "Siren"), LabelConfidenceLevel
// the score, taxonomy the flat ["sound", <class>]. Per the OSD-IID rule that an
// empty identification is not representable, when nothing is classified it returns
// a coarse ["sound"] candidate rather than an empty identifier.
//
// The Audio Scene Descriptors (OSD-BAS) the objects stand in come in and go out unchanged, so that whoever follows has the scene and the identifiers together. The AIM identifies every object that arrives
// (the runner), one Instance Identifier for each; run as a processor for one Message, it identifies the one object the Message carries.
public sealed class CaeAiiAimProcessor : IAimProcessor, IAimRunner
{
    private const string IidHeader = "OSD-IID-V1.5";

    private readonly string _instanceId;
    private readonly SoundClassifier _classifier;
    private readonly string _inPort;    // OSD-BAO (mono-16k, from CAE-QCV)
    private readonly string _outPort;   // OSD-IID
    private readonly string _sceneIn, _sceneOut;   // OSD-BAS, in and out
    private const string Bao = "OSD-BAO-V1.5", Bas = "OSD-BAS-V1.5";

    public CaeAiiAimProcessor(string instanceId, SoundClassifier classifier, AimPortReader ports)
    {
        _instanceId = instanceId;
        _classifier = classifier;
        _inPort     = ports.Input("OSD-BAO-V1.5");
        _outPort    = ports.Output("OSD-IID-V1.5");
        _sceneIn    = ports.Input(Bas);
        _sceneOut   = ports.Output(Bas);
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        var hasObject = message.Ports.TryGetValue(_inPort, out var json) && !string.IsNullOrWhiteSpace(json);
        var hasScene = message.Ports.TryGetValue(_sceneIn, out var scene) && !string.IsNullOrWhiteSpace(scene);
        if (!hasObject && !hasScene)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Basic Audio Object on input port"));

        var ports = new Dictionary<string, string>();
        if (hasScene) ports[_sceneOut] = scene!;                                    // the scene goes on as it came
        if (hasObject)
        {
            var iid = Identify(json!, out var error);
            if (iid is null) return Task.FromResult(Message.Error(message.MessageId, _instanceId, error!));
            ports[_outPort] = MpaiJson.ToJson(iid);
        }
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = ports });
    }

    // THE RUNNER: the scenes that arrive go on as they are, and every object that arrives is identified, one Instance Identifier for each.
    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Bas, 1), (Bao, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } message) continue;
            if (port.DataType == Bas) { await ports.WriteAsync(Bas, 1, message.Json); continue; }
            var iid = Identify(message.Json, out var error);
            if (iid is null) { context.Report(error!); continue; }
            await ports.WriteAsync("OSD-IID-V1.5", 1, MpaiJson.ToJson(iid));
        }
    }

    // What the sound IS: the Instance Identifier of one Basic Audio Object, or the reason it could not be given.
    private InstanceIdentifier? Identify(string json, out string? error)
    {
        error = null;
        var bao = MpaiJson.FromJson<BasicAudioObject>(json);
        if (bao is null) { error = "could not parse Basic Audio Object"; return null; }

        // The audio is mono-16k-16bit PCM (CAE-QCV normalised it): read samples directly.
        var samples = ReadMono16kSamples(bao);
        if (samples is { Length: > 0 })
        {
            var top = _classifier.Classify(samples).FirstOrDefault();
            return top is not null ? Identify(top.Label, top.Score) : Coarse();
        }
        return Coarse();
    }

    // Read 16-bit PCM samples (from the inline data QCV produced) as float[-1,1].
    private static float[]? ReadMono16kSamples(BasicAudioObject bao)
    {
        var inline = bao.BasicAudioObjectData.OfType<InlineAudioData>().FirstOrDefault();
        if (inline is null || string.IsNullOrWhiteSpace(inline.Data)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(inline.Data); } catch { return null; }
        int n = bytes.Length / 2;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static InstanceIdentifier Identify(string soundClass, float score) => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = soundClass,
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = soundClass,
                LabelConfidenceLevel = score,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "sound", soundClass } }
            }
        }
    };

    private static InstanceIdentifier Coarse() => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = "sound",
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = "sound",
                LabelConfidenceLevel = 0f,
                Taxonomy = new InstanceTaxonomy { TaxonomyLevelIDs = { "sound" } }
            }
        }
    };
}
