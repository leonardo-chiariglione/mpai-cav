"""The voice engine of the Speech Model: OpenVoice V2 tone-colour converter (MIT), run from the C# AIMs as a process.

  extract --out se.json a.wav [b.wav ...]       the speaker embedding (256 numbers) of the speech in the files: the SPEECH MODEL
  convert --src se.json --tgt se.json in.wav out.wav [--tau 0.3]    in.wav, with the voice of the source embedding, in the voice of the target
  clone --tgt se.json [--tau 0.3] in1.wav out1.wav [in2.wav out2.wav ...]    each in.wav in the voice of the target; the voice it has is its own embedding (the model is loaded once)

Only torch, numpy, scipy and soundfile are needed: the OpenVoice package asks for librosa and wavmark (the latter only for its watermark) and for old versions of both, which this does
not use; the two are replaced here by a stub (librosa.filters, which the spectrogram does not call) and by scipy (resampling).
Environment: OPENVOICE_HOME, the folder with src/OpenVoice-main and converter/{config.json,checkpoint.pth} (default D:\\AI\\openvoice).
"""
import argparse, json, os, sys, types
import numpy as np
import soundfile
import torch
from scipy.signal import resample_poly
from math import gcd

HOME = os.environ.get("OPENVOICE_HOME", r"D:\AI\openvoice")
sys.path.insert(0, os.path.join(HOME, "src", "OpenVoice-main"))
for name in ("librosa", "librosa.filters"):          # mel_processing imports a function it never calls for the spectrogram
    sys.modules.setdefault(name, types.ModuleType(name))
sys.modules["librosa.filters"].mel = None
sys.modules["librosa"].filters = sys.modules["librosa.filters"]

# openvoice/__init__.py needs nothing; the modules are imported one by one so that api.py (librosa, wavmark, text) is not
import importlib.util
def _load(name):
    spec = importlib.util.spec_from_file_location("openvoice." + name, os.path.join(HOME, "src", "OpenVoice-main", "openvoice", name + ".py"))
    module = importlib.util.module_from_spec(spec); sys.modules["openvoice." + name] = module; spec.loader.exec_module(module); return module
sys.modules["openvoice"] = types.ModuleType("openvoice"); sys.modules["openvoice"].__path__ = [os.path.join(HOME, "src", "OpenVoice-main", "openvoice")]
for part in ("commons", "transforms", "attentions", "modules", "models", "mel_processing", "utils"):
    setattr(sys.modules["openvoice"], part, _load(part))
models, mel, utils = sys.modules["openvoice.models"], sys.modules["openvoice.mel_processing"], sys.modules["openvoice.utils"]


def load_model():
    hps = utils.get_hparams_from_file(os.path.join(HOME, "converter", "config.json"))
    model = models.SynthesizerTrn(len(getattr(hps, "symbols", [])), hps.data.filter_length // 2 + 1, n_speakers=hps.data.n_speakers, **hps.model)
    state = torch.load(os.path.join(HOME, "converter", "checkpoint.pth"), map_location="cpu")
    model.load_state_dict(state["model"], strict=False)
    return model.eval(), hps


def read(path, rate):
    audio, sr = soundfile.read(path, dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if sr != rate:
        g = gcd(sr, rate); audio = resample_poly(audio, rate // g, sr // g).astype(np.float32)
    return audio


def spectrogram(audio, hps):
    y = torch.from_numpy(audio).unsqueeze(0)
    return mel.spectrogram_torch(y, hps.data.filter_length, hps.data.sampling_rate, hps.data.hop_length, hps.data.win_length, center=False)


def extract(files):
    model, hps = load_model(); gs = []
    with torch.no_grad():
        for f in files:
            spec = spectrogram(read(f, hps.data.sampling_rate), hps)
            gs.append(model.ref_enc(spec.transpose(1, 2)).unsqueeze(-1))
    return torch.stack(gs).mean(0).reshape(-1).tolist()


def convert(src, tgt, source_wav, out_wav, tau):
    model, hps = load_model()
    s = torch.tensor(src, dtype=torch.float32).reshape(1, -1, 1); t = torch.tensor(tgt, dtype=torch.float32).reshape(1, -1, 1)
    spec = spectrogram(read(source_wav, hps.data.sampling_rate), hps)
    with torch.no_grad():
        audio = model.voice_conversion(spec, torch.LongTensor([spec.size(-1)]), sid_src=s, sid_tgt=t, tau=tau)[0][0, 0].float().numpy()
    soundfile.write(out_wav, audio, hps.data.sampling_rate)


def clone(tgt, pairs, tau):
    model, hps = load_model(); t = torch.tensor(tgt, dtype=torch.float32).reshape(1, -1, 1)
    for source_wav, out_wav in pairs:
        audio = read(source_wav, hps.data.sampling_rate); spec = spectrogram(audio, hps)
        with torch.no_grad():
            s = model.ref_enc(spec.transpose(1, 2)).unsqueeze(-1)
            out = model.voice_conversion(spec, torch.LongTensor([spec.size(-1)]), sid_src=s, sid_tgt=t, tau=tau)[0][0, 0].float().numpy()
        soundfile.write(out_wav, out, hps.data.sampling_rate)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(); sub = ap.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("extract"); e.add_argument("--out", required=True); e.add_argument("files", nargs="+")
    c = sub.add_parser("convert"); c.add_argument("--src", required=True); c.add_argument("--tgt", required=True); c.add_argument("--tau", type=float, default=0.3); c.add_argument("input"); c.add_argument("output")
    k = sub.add_parser("clone"); k.add_argument("--tgt", required=True); k.add_argument("--tau", type=float, default=0.3); k.add_argument("files", nargs="+")
    a = ap.parse_args()
    if a.cmd == "extract":
        json.dump({"se": extract(a.files)}, open(a.out, "w"))
    elif a.cmd == "clone":
        clone(json.load(open(a.tgt))["se"], list(zip(a.files[0::2], a.files[1::2])), a.tau)
    else:
        convert(json.load(open(a.src))["se"], json.load(open(a.tgt))["se"], a.input, a.output, a.tau)
