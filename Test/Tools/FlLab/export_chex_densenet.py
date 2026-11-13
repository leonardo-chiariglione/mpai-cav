# Run: Support/Python/pyannote-venv/Scripts/python.exe -I export_chex_densenet.py <weights dir> <out.onnx>
#
# AIH-HSP, night request N-22: the FROZEN part of the NN Model for real chest X-rays. TorchXRayVision DenseNet-121 (code Apache-2.0) with the weights trained on
# CheXpert only ("densenet121-res224-chex"): not on NIH ChestX-ray14, whose images are the check set. The ONNX file gives, for each image, the 1024 features of the
# network (DenseNet features, ReLU, global average pool), not its 18 findings: the Front Ends train the head on these features.
#
# Input "image": n x 1 x 224 x 224, float, a grey image scaled as TorchXRayVision expects (xrv.datasets.normalize: pixel 0..255 -> -1024..1024), centre-cropped and
# resized to 224. Output "features": n x 1024.
import sys, torch
import torchxrayvision as xrv

weights_dir, out = sys.argv[1], sys.argv[2]
model = xrv.models.DenseNet(weights="densenet121-res224-chex", cache_dir=weights_dir).eval()


class Features(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, x):
        return self.m.features2(x)


net = Features(model).eval()
x = torch.zeros(2, 1, 224, 224)
torch.manual_seed(0)
x.uniform_(-1024, 1024)
with torch.no_grad():
    ref = net(x)
print("features", tuple(ref.shape))

# the package `onnx` is not installed and nothing more is downloaded: the exporter needs it only to add onnxscript functions, of which this graph has none
from torch.onnx._internal.torchscript_exporter import onnx_proto_utils
onnx_proto_utils._add_onnxscript_fn = lambda proto, custom_opsets: proto
torch.onnx.export(net, x, out, input_names=["image"], output_names=["features"],
                  dynamic_axes={"image": {0: "n"}, "features": {0: "n"}}, opset_version=17, dynamo=False)

# The reference output of the two test images, for the check by ONNX Runtime (in C#).
x.numpy().astype("<f4").tofile(out + ".check-x.bin"); ref.numpy().astype("<f4").tofile(out + ".check-features.bin")
print("written", out)
