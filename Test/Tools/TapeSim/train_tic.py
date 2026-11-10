"""Trains the Tape Irregularity Classifier on the simulated dataset (make_dataset.py) and exports it to ONNX.

  <venv python> -I train_tic.py [--epochs 40]

THE MODEL IS TRAINED ON SIMULATION, NOT ON REAL TAPES (see tape_sim.py).

Model: the whole path from the waveform is in the ONNX graph (no DSP in the AIM):
  waveform [batch, 64000] (mono, 16 kHz, -1..1)
  -> fixed DFT as a convolution (Hann 400 samples = 25 ms, hop 160 = 10 ms) -> power -> 64 mel bands (125-7500 Hz) -> log
  -> minus the mean of the clip (the level of the clip is no cue)
  -> 4 convolution blocks -> mean over the mel axis -> 1-D convolution -> 6 time bins -> 8 logits.
Classes: tape_sim.CLASSES.

Writes (in Test/Data/Music/dataset/): tic.pt, TapeIrregularityClassifier.onnx, tic-report.json (accuracy, confusion matrices, per group), tic-train.log.
"""
import argparse
import json
import math
import os
import sys
import time

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import tape_sim as ts  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
DATA = os.path.join(ROOT, 'Test', 'Data', 'Music', os.environ.get('TIC_DATASET', 'dataset'))
WIN, HOP, NFFT_BINS, MELS = 400, 160, 201, 64          # 400-sample window: 201 bins up to 8 kHz


def mel_matrix(rate=16000, nfft=400, mels=MELS, fmin=125.0, fmax=7500.0):
    def hz2mel(f): return 2595 * np.log10(1 + f / 700)
    def mel2hz(m): return 700 * (10 ** (m / 2595) - 1)
    pts = mel2hz(np.linspace(hz2mel(fmin), hz2mel(fmax), mels + 2))
    freqs = np.arange(nfft // 2 + 1) * rate / nfft
    m = np.zeros((mels, len(freqs)), np.float32)
    for i in range(mels):
        lo, mid, hi = pts[i], pts[i + 1], pts[i + 2]
        up = (freqs - lo) / (mid - lo)
        down = (hi - freqs) / (hi - mid)
        m[i] = np.maximum(0, np.minimum(up, down))
    m /= np.maximum(m.sum(axis=1, keepdims=True), 1e-9)
    return m


class Frontend(nn.Module):
    """waveform [B, N] -> log-mel [B, 1, 64, frames], the mean of the clip taken away."""
    def __init__(self):
        super().__init__()
        n = np.arange(WIN)
        window = 0.5 - 0.5 * np.cos(2 * np.pi * n / WIN)
        k = np.arange(WIN // 2 + 1)[:, None]
        cos = np.cos(2 * np.pi * k * n / WIN) * window
        sin = -np.sin(2 * np.pi * k * n / WIN) * window
        self.register_buffer('kernel', torch.tensor(np.concatenate([cos, sin]), dtype=torch.float32).unsqueeze(1))   # [402, 1, 400]
        self.register_buffer('mel', torch.tensor(mel_matrix()))                                                      # [64, 201]

    def forward(self, wave):
        x = wave.unsqueeze(1)
        spec = F.conv1d(x, self.kernel, stride=HOP)                      # [B, 402, frames]
        re, im = spec[:, :NFFT_BINS], spec[:, NFFT_BINS:]
        power = re * re + im * im                                        # [B, 201, frames]
        mel = torch.matmul(self.mel, power)                              # [B, 64, frames]
        logmel = torch.log(mel + 1e-6)
        logmel = logmel - logmel.mean(dim=(1, 2), keepdim=True)
        return logmel.unsqueeze(1)


def block(i, o):
    return nn.Sequential(nn.Conv2d(i, o, 3, padding=1), nn.BatchNorm2d(o), nn.ReLU(), nn.MaxPool2d(2))


class Head(nn.Module):
    def __init__(self, classes=len(ts.CLASSES)):
        super().__init__()
        self.blocks = nn.Sequential(block(1, 16), block(16, 32), block(32, 64), block(64, 64))   # 64x398 -> 4x24
        self.temporal = nn.Sequential(nn.Conv1d(64, 96, 3, padding=1), nn.ReLU(), nn.AvgPool1d(4))   # 24 -> 6
        self.drop = nn.Dropout(0.3)
        self.out = nn.Linear(96 * 6, classes)

    def forward(self, logmel):
        x = self.blocks(logmel)                                          # [B, 64, 4, 24]
        x = x.mean(dim=2)                                                # [B, 64, 24]
        x = self.temporal(x)                                             # [B, 96, 6]
        return self.out(self.drop(x.flatten(1)))


class Tic(nn.Module):
    def __init__(self):
        super().__init__()
        self.frontend = Frontend()
        self.head = Head()

    def forward(self, wave):
        return self.head(self.frontend(wave))


def features(model, clips, batch=64):
    out = []
    with torch.no_grad():
        for i in range(0, len(clips), batch):
            w = torch.tensor(clips[i:i + batch].astype(np.float32) / 32768)
            out.append(model.frontend(w).half())
    return torch.cat(out)


def predict(head, feats, batch=256):
    head.eval()
    out = []
    with torch.no_grad():
        for i in range(0, len(feats), batch):
            out.append(head(feats[i:i + batch].float()))
    return torch.cat(out)


def confusion(truth, pred, n=len(ts.CLASSES)):
    m = np.zeros((n, n), int)
    for t, p in zip(truth, pred):
        m[t, p] += 1
    return m


def summarise(name, truth, pred, meta):
    m = confusion(truth, pred)
    acc = float((np.array(truth) == np.array(pred)).mean())
    per_class = {c: {'recall': float(m[i, i] / max(m[i].sum(), 1)), 'precision': float(m[i, i] / max(m[:, i].sum(), 1))} for i, c in enumerate(ts.CLASSES)}
    groups = {}
    for t, p, mt in zip(truth, pred, meta):
        g = groups.setdefault(mt['group'], [0, 0])
        g[0] += int(t == p); g[1] += 1
    return {'accuracy': acc, 'confusion': {'rows_are_truth': ts.CLASSES, 'columns_are_prediction': ts.CLASSES, 'matrix': m.tolist()}, 'per_class': per_class,
            'accuracy_by_group': {g: v[0] / v[1] for g, v in sorted(groups.items())}}


def export_onnx(model, path):
    """The package `onnx` is not installed here and nothing is downloaded for it: the exporter of torch needs it only to add functions of
    onnxscript to the graph, of which this one has none; that step is skipped."""
    from torch.onnx._internal.torchscript_exporter import onnx_proto_utils
    onnx_proto_utils._add_onnxscript_fn = lambda proto, custom_opsets: proto
    model.eval()
    torch.onnx.export(model, torch.zeros(1, ts.N), path, input_names=['waveform'], output_names=['logits'], dynamic_axes={'waveform': {0: 'batch'}, 'logits': {0: 'batch'}},
                      opset_version=17, dynamo=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--export-only', action='store_true')
    ap.add_argument('--epochs', type=int, default=40)
    ap.add_argument('--seed', type=int, default=11)
    args = ap.parse_args()
    if args.export_only:
        m = Tic(); m.load_state_dict(torch.load(os.path.join(DATA, 'tic.pt'))); export_onnx(m, os.path.join(DATA, 'TapeIrregularityClassifier.onnx')); return
    torch.manual_seed(args.seed); np.random.seed(args.seed)
    torch.set_num_threads(max(1, (os.cpu_count() or 4) - 2))
    log = open(os.path.join(DATA, 'tic-train.log'), 'w', encoding='utf-8')
    def say(*a):
        s = ' '.join(str(x) for x in a); print(s, flush=True); log.write(s + '\n'); log.flush()

    model = Tic()
    index = {c: i for i, c in enumerate(ts.CLASSES)}
    sets = {}
    t0 = time.time()
    for name in ('train', 'val', 'test_seen', 'test_unseen'):
        clips = np.load(os.path.join(DATA, name + '.npy'), mmap_mode='r')
        meta = json.load(open(os.path.join(DATA, name + '.json'), encoding='utf-8'))
        y = torch.tensor([index[m['class']] for m in meta])
        sets[name] = (features(model, np.asarray(clips)), y, meta)
        say(name, tuple(sets[name][0].shape), f'{time.time() - t0:.0f} s')

    xt, yt, _ = sets['train']
    head = model.head
    opt = torch.optim.AdamW(head.parameters(), lr=2e-3, weight_decay=1e-2)
    steps = args.epochs * math.ceil(len(xt) / 64)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=3e-3, total_steps=steps)
    best, best_state = -1, None
    for epoch in range(1, args.epochs + 1):
        head.train()
        perm = torch.randperm(len(xt))
        total = 0.0
        for i in range(0, len(xt), 64):
            idx = perm[i:i + 64]
            x = xt[idx].float()
            x = x + 0.05 * torch.randn(x.shape[0], 1, x.shape[2], 1)       # a slight random tilt of the bands
            loss = F.cross_entropy(head(x), yt[idx], label_smoothing=0.05)
            opt.zero_grad(); loss.backward(); opt.step(); sched.step()
            total += float(loss) * len(idx)
        xv, yv, _ = sets['val']
        acc = float((predict(head, xv).argmax(1) == yv).float().mean())
        say(f'epoch {epoch:2d} loss {total / len(xt):.4f} val {acc:.4f} {time.time() - t0:.0f} s')
        if acc > best:
            best, best_state = acc, {k: v.clone() for k, v in head.state_dict().items()}
    head.load_state_dict(best_state)
    say(f'best val {best:.4f}')

    report = {'note': 'TRAINED ON SIMULATED IMPAIRMENTS OF MUSIC, NOT ON REAL TAPES. Pieces are split train/val/test; no piece is in two sets.',
              'classes': ts.CLASSES, 'epochs': args.epochs, 'seed': args.seed, 'best_val_accuracy': best,
              'train_pieces': len({m['piece'] for m in sets['train'][2]}), 'val_pieces': len({m['piece'] for m in sets['val'][2]}), 'test_pieces': len({m['piece'] for m in sets['test_seen'][2]}),
              'clips': {k: len(v[1]) for k, v in sets.items()}, 'params': ts.PARAMS}
    for name in ('val', 'test_seen', 'test_unseen'):
        x, y, meta = sets[name]
        pred = predict(head, x).argmax(1).tolist()
        report[name] = summarise(name, y.tolist(), pred, meta)
        say(name, 'accuracy', f"{report[name]['accuracy']:.4f}")
        for c, v in report[name]['per_class'].items():
            say(f'   {c:5s} recall {v["recall"]:.3f} precision {v["precision"]:.3f}')
    # unseen parameters, broken down by impairment parameter
    x, y, meta = sets['test_unseen']
    pred = predict(head, x).argmax(1)
    detail = {}
    for k, (p, t, m) in enumerate(zip(pred.tolist(), y.tolist(), meta)):
        if m['class'] == 'ssv':
            detail.setdefault('ssv_octaves_%+d' % m['octaves'], []).append(int(p == t))
        if m['class'] == 'esv':
            detail.setdefault('esv_speed_%g_%s_read_as_%s' % (m['speed'], m['write'], m['read']), []).append(int(p == t))
        if m['class'] == 'da':
            detail.setdefault('da_' + m['kind'], []).append(int(p == t))
    report['test_unseen']['by_parameter'] = {k: {'n': len(v), 'recall': sum(v) / len(v)} for k, v in sorted(detail.items())}
    json.dump(report, open(os.path.join(DATA, 'tic-report.json'), 'w', encoding='utf-8'), indent=1)

    torch.save(model.state_dict(), os.path.join(DATA, 'tic.pt'))
    path = os.path.join(DATA, 'TapeIrregularityClassifier.onnx')
    export_onnx(model, path)
    say('exported', path, os.path.getsize(path), 'bytes')


if __name__ == '__main__':
    main()
