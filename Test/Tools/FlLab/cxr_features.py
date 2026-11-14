# Run: Support/Python/pyannote-venv/Scripts/python.exe -I cxr_features.py <Test/Data/ChestXray> <Test/Data/Fl/txv> <Test/Data/Fl/cxr-head.onnx>
#
# AIH-HSP, night request N-22. ONCE, for the federated round on real NIH ChestX-ray14 images:
#  1. unpacks images_001.tar.gz into <ChestXray>/images (git-ignored), where it is not there yet;
#  2. computes the 1024 DenseNet-121 features of every image (TorchXRayVision, weights "densenet121-res224-chex", trained on CheXpert only - the same network as
#     Test/Data/Fl/chex-densenet121-features.onnx, checked by AihChestNetworkTests) with the preprocessing of TorchXRayVision: grey, xrv normalize (0..255 ->
#     -1024..1024), centre crop, resize to 224; writes <ChestXray>/features/cxr-features.bin (n x 1024 float32, little endian) and cxr-index.csv (one line per image:
#     name, patient, age, sex, view, any finding 0/1, effusion 0/1, in the official test list 0/1), in the same order;
#  3. writes, for the first two images, the preprocessed input and its features (cxr-check-x.bin, cxr-check-features.bin): a test runs the ONNX file on that input
#     with ONNX Runtime and finds the cached features;
#  4. writes the HEAD that the Front Ends train (the network of the NN Model that travels): Mul by a fixed gain (so that its input is of the order of 1), Gemm
#     1024 -> 64, Relu. Its starting weights are random (seed 22).
import csv, os, sys, tarfile, time
import numpy as np
import torch, torch.nn as nn
import skimage.io
import torchxrayvision as xrv

data, weights_dir, head_out = sys.argv[1], sys.argv[2], sys.argv[3]
images = os.path.join(data, "images")
out = os.path.join(data, "features")
os.makedirs(out, exist_ok=True)

if not os.path.isdir(images) or len(os.listdir(images)) < 4999:
    t = time.time()
    with tarfile.open(os.path.join(data, "images_001.tar.gz"), "r:gz") as tar:
        tar.extractall(data, filter="data")
    print(f"unpacked in {time.time() - t:.0f} s", flush=True)

names = sorted(n for n in os.listdir(images) if n.endswith(".png"))
print("images", len(names), flush=True)
rows = {}
with open(os.path.join(data, "Data_Entry_2017_v2020.csv"), newline="") as f:
    for r in csv.DictReader(f):
        rows[r["Image Index"]] = r
with open(os.path.join(data, "test_list.txt")) as f:
    test = set(l.strip() for l in f if l.strip())

torch.set_num_threads(os.cpu_count() or 4)
model = xrv.models.DenseNet(weights="densenet121-res224-chex", cache_dir=weights_dir).eval()
crop, resize = xrv.datasets.XRayCenterCrop(), xrv.datasets.XRayResizer(224, engine="skimage")


def prepare(path):
    img = skimage.io.imread(path)
    if img.ndim == 3:
        img = img[:, :, :3].mean(2)
    img = xrv.datasets.normalize(img.astype(np.float32), 255)
    img = resize(crop(img[None, ...]))
    return img.astype(np.float32)


feats = np.zeros((len(names), 1024), dtype=np.float32)
batch = 32
t = time.time()
check_x = None
with torch.no_grad():
    for at in range(0, len(names), batch):
        x = np.stack([prepare(os.path.join(images, n)) for n in names[at:at + batch]])
        if at == 0:
            check_x = x[:2].copy()
        feats[at:at + len(x)] = model.features2(torch.from_numpy(x)).numpy()
        if at % 640 == 0:
            print(f"{at + len(x)} / {len(names)}  {time.time() - t:.0f} s", flush=True)

feats.astype("<f4").tofile(os.path.join(out, "cxr-features.bin"))
with open(os.path.join(out, "cxr-index.csv"), "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["Image", "Patient", "Age", "Sex", "View", "Any", "Effusion", "InTestList"])
    for n in names:
        r = rows[n]
        labels = r["Finding Labels"].split("|")
        w.writerow([n, r["Patient ID"], r["Patient Age"], r["Patient Sex"], r["View Position"],
                    0 if labels == ["No Finding"] else 1, 1 if "Effusion" in labels else 0, 1 if n in test else 0])
check_x.astype("<f4").tofile(os.path.join(out, "cxr-check-x.bin"))
feats[:2].astype("<f4").tofile(os.path.join(out, "cxr-check-features.bin"))
print(f"features written in {time.time() - t:.0f} s; mean {feats.mean():.4f} std {feats.std():.4f}", flush=True)

# THE HEAD
torch.manual_seed(22)


class Gain(nn.Module):
    def __init__(self, g):
        super().__init__()
        self.g = float(g)

    def forward(self, x):
        return x * self.g


gain = 1.0 / float(feats.std())
head = nn.Sequential(Gain(gain), nn.Linear(1024, 64), nn.ReLU()).eval()
from torch.onnx._internal.torchscript_exporter import onnx_proto_utils
onnx_proto_utils._add_onnxscript_fn = lambda proto, custom_opsets: proto
torch.onnx.export(head, torch.zeros(1, 1024), head_out, input_names=["features"], output_names=["head"],
                  dynamic_axes={"features": {0: "n"}, "head": {0: "n"}}, opset_version=17, dynamo=False)
print("head written", head_out, "gain", gain, flush=True)
