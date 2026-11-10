# Frozen feature network for the federated-learning demo (AIH-HSP, night N-1).
# STAND-IN: a convolutional network with FIXED RANDOM weights (seed 7), never trained. It takes a 1x64x64 grey image in [0,1] and gives 64 features.
# A real demo would put here a network pretrained on chest X-rays (to be obtained by the author; nothing is downloaded by the night job).
# Run: Support/Python/pyannote-venv/Scripts/python.exe -I make_frozen_backbone.py <out.onnx>
import sys, torch, torch.nn as nn
torch.manual_seed(7)
class Gain(nn.Module):
    def __init__(self, g): super().__init__(); self.g = float(g)
    def forward(self, x): return x * self.g
net = nn.Sequential(
    nn.Conv2d(1, 8, 5, stride=2, padding=2), nn.ReLU(),
    nn.Conv2d(8, 16, 3, stride=2, padding=1), nn.ReLU(),
    nn.Conv2d(16, 32, 3, stride=2, padding=1), nn.ReLU(),     # 32 x 8 x 8
    nn.AdaptiveAvgPool2d(2), nn.Flatten(),                      # 32*4 = 128
    nn.Linear(128, 64), nn.ReLU()).eval()
for p in net.parameters(): p.requires_grad_(False)
# a fixed gain, so that the features are of the order of 1 on pictures like the ones of the demo (grey 0.2..0.8 with smooth structure); chosen once, never trained
with torch.no_grad():
    probe = torch.nn.functional.interpolate(torch.rand(64, 1, 8, 8) * 0.6 + 0.2, size=64, mode='bilinear')
    gain = 1.0 / net(probe).std(dim=0).mean().item()
net = nn.Sequential(*net, Gain(gain)).eval()
# the package `onnx` is not installed and nothing is downloaded: the exporter needs it only to add onnxscript functions, of which this graph has none (as in TapeSim/train_tic.py)
from torch.onnx._internal.torchscript_exporter import onnx_proto_utils
onnx_proto_utils._add_onnxscript_fn = lambda proto, custom_opsets: proto
x = torch.zeros(1, 1, 64, 64)
torch.onnx.export(net, x, sys.argv[1], input_names=["image"], output_names=["features"],
                  dynamic_axes={"image": {0: "n"}, "features": {0: "n"}}, opset_version=17, dynamo=False)
print("written", sys.argv[1])
