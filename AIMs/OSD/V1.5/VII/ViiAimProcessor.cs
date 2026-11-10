// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Linq;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Osd.VisualScene;   // YoloxObjectDetector, ObjectDetection

namespace Mpai.Osd.Vii;

// CVE-VII-V1.0 - Visual Instance Identification, as an AIF IAimProcessor.
//
// Receives a Basic Visual Object (the Target Visual Object to be identified) and
// produces its Visual Instance Identifier (OSD-IID): the identity of the object
// against a taxonomy. It is the general-object analogue of PAF-FIR - where FIR
// identifies a located FACE object, VII identifies a located visual object of any
// COCO class. The scene has already LOCATED the object (BVS locates the BVOs in
// OSD-BMS = BAS + BSS + BVS); VII's narrow job is to say WHAT that located object
// is.
//
// Engine: YOLOX (yolox_s.onnx). The BVO carries one object's image; YOLOX runs on
// it and the highest-confidence detection becomes the instance identity. The IID's
// InstanceLabel is the COCO class (e.g. "zebra"), LabelConfidenceLevel the detector
// score, and the taxonomy path is the flat ["visual","object",<class>] (a pragmatic
// placeholder until a formal MPAI visual taxonomy is available - a real taxonomy
// URI would go in Taxonomy.TaxonomyDataURI).
//
// "Empty identification is not representable" (OSD-IID rule): if no object is
// detected, VII returns a coarse candidate ["visual","object"] labelled "object"
// with the top score (or zero), rather than an empty identifier.
//
// The Basic Visual Scene Descriptors (OSD-BVS) the objects stand in come in and go out unchanged, so that whoever follows has the scene and the identifiers together. The AIM identifies every object that arrives
// (the runner), one Instance Identifier for each; run as a processor for one Message, it identifies the one object the Message carries.
public sealed class ViiAimProcessor : IAimProcessor, IAimRunner
{
    private const string IidHeader = "OSD-IID-V1.5";

    private readonly string _instanceId;
    private readonly YoloxObjectDetector _detector;
    private readonly string _inPort;
    private readonly string _outPort;
    private readonly string _sceneIn, _sceneOut;
    private const string Bvo = "OSD-BVO-V1.5", Bvs = "OSD-BVS-V1.5";

    public ViiAimProcessor(
        string instanceId,
        YoloxObjectDetector detector,
        AimPortReader ports)
    {
        _instanceId = instanceId;
        _detector   = detector;
        _inPort     = ports.Input("OSD-BVO-V1.5");
        _outPort    = ports.Output("OSD-IID-V1.5");
        _sceneIn    = ports.Input(Bvs);
        _sceneOut   = ports.Output(Bvs);
    }

    public string InstanceId => _instanceId;

    public System.Threading.Tasks.Task<Message> ProcessAsync(Message message)
    {
        var hasObject = message.Ports.TryGetValue(_inPort, out var bvoJson);
        var hasScene = message.Ports.TryGetValue(_sceneIn, out var scene) && !string.IsNullOrWhiteSpace(scene);
        if (!hasObject && !hasScene)
            return System.Threading.Tasks.Task.FromResult(
                Message.Error(message.MessageId, _instanceId, "no Target Visual Object on input port"));

        var ports = new System.Collections.Generic.Dictionary<string, string>();
        if (hasScene) ports[_sceneOut] = scene!;                                    // the scene goes on as it came
        if (hasObject)
        {
            var iid = Identify(bvoJson!, out var error);
            if (error is not null) return System.Threading.Tasks.Task.FromResult(Message.Error(message.MessageId, _instanceId, error));
            if (iid is not null) ports[_outPort] = MpaiJson.ToJson(iid);            // none for a Face: FIR's
        }
        return System.Threading.Tasks.Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = ports });
    }

    // THE RUNNER: the scenes that arrive go on as they are, and every object that arrives is identified, one Instance Identifier for each.
    public async System.Threading.Tasks.Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (Bvs, 1), (Bvo, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } message) continue;
            if (port.DataType == Bvs) { await ports.WriteAsync(Bvs, 1, message.Json); continue; }
            var iid = Identify(message.Json, out var error);
            if (error is not null) { context.Report(error); continue; }
            if (iid is not null) await ports.WriteAsync("OSD-IID-V1.5", 1, MpaiJson.ToJson(iid));
        }
    }

    // What the object IS: its Instance Identifier; null and no error for a Face (FIR's), the reason where it could not be given.
    private InstanceIdentifier? Identify(string bvoJson, out string? error)
    {
        error = null;
        var visual = Mpai.Aif.PortData.PortObjects.ReadVisual(bvoJson);
        if (visual is null || visual.Data.Length == 0) { error = "empty Target Visual Object"; return null; }

        // Type guard: VII identifies NON-FACE visual objects. Faces are FIR's; a
        // face-tagged object is declined here so EXACTLY ONE Instance Identifier is
        // produced per object. Untagged objects default to VII (generic).
        if (visual.VisualQualifier?.Attributes?.VisualObjectType == "Face") return null;

        // Identify the object: run YOLOX, take the highest-confidence detection.
        var detections = _detector.Detect(visual.Data);
        var best = detections.OrderByDescending(d => d.Score).FirstOrDefault();
        return best is not null ? Identify(best.ClassName, best.Score) : Coarse();
    }

    // A confident identification: label = COCO class, taxonomy = [visual, object, class].
    private static InstanceIdentifier Identify(string className, float score) => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = className,
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = className,
                LabelConfidenceLevel = score,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = { "visual", "object", className }
                }
            }
        }
    };

    // No object detected: a coarse candidate, so the identifier is never empty.
    private static InstanceIdentifier Coarse() => new()
    {
        Header = IidHeader,
        InstanceIdentifier_ = "object",
        InstanceIdentifierData =
        {
            new InstanceCandidate
            {
                InstanceLabel = "object",
                LabelConfidenceLevel = 0f,
                Taxonomy = new InstanceTaxonomy
                {
                    TaxonomyLevelIDs = { "visual", "object" }
                }
            }
        }
    };
}
