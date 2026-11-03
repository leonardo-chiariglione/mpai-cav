using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.PhysicalLayer;

// 3D Model Object Delivery (UNT-3OD): a Delivery Unit of the User Agent, not an AIM.
// It delivers a 3D scene to a 3D renderer: the 3D Model together with the animation
// streams that drive it - a Face animation (Face Descriptors) and a Body animation
// (Body Descriptors), each optional and independent. The renderer combines model and
// animation and renders (3D to 2D); posing and rendering are one act. Further animation
// streams can be added as overloads without disturbing the model delivery.
public interface I3DModelDelivery
{
    Task DeliverAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation = null,
        BodyDescriptorsObject? bodyAnimation = null);
}

// A headless 3D Model Object Delivery Unit: reports the 3D scene it would render (the
// model plus which animation streams drive it) rather than driving a display. The
// graphical Unit (a WebView-backed 3D renderer) is WebView3DModelDelivery.
public sealed class ConsoleModelDelivery : I3DModelDelivery
{
    public Task DeliverAsync(
        Basic3DModelObject model,
        FaceDescriptorsObject? faceAnimation = null,
        BodyDescriptorsObject? bodyAnimation = null)
    {
        var anim = "";
        if (faceAnimation is not null) anim += " + face animation";
        if (bodyAnimation is not null) anim += " + body animation";
        AimLog.Write("UNT-3OD-V1.0",
            $"rendering 3D scene: model {model.Basic3DModelObjectID} ({model.Data.Length:N0} bytes){anim}");
        return Task.CompletedTask;
    }
}
