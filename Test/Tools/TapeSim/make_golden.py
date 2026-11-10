"""Writes the golden clips of the C# tests of the Tape Irregularity Classification: one clip of each class from the test pieces (pieces the model
was not trained on), picked among those the model classifies right with the highest probability, as WAV files (16 kHz, 16-bit, 4 s) with the
probabilities PyTorch gives -- the C# test checks the ONNX model in the AIM gives the same.

  <venv python> -I make_golden.py        writes Test/Data/Music/tic-golden/ (8 WAV files of 128 kB and expected.json)
THE CLIPS ARE SIMULATED IMPAIRMENTS OF PUBLIC-DOMAIN / CC0 MUSIC (see tape_sim.py).
"""
import json
import os
import sys

import numpy as np
import soundfile as sf
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import tape_sim as ts  # noqa: E402
import train_tic as tt  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
DATA = os.path.join(ROOT, 'Test', 'Data', 'Music', 'dataset')
OUT = os.path.join(ROOT, 'Test', 'Data', 'Music', 'tic-golden')


def main():
    model = tt.Tic()
    model.load_state_dict(torch.load(os.path.join(DATA, 'tic.pt')))
    model.eval()
    clips = np.load(os.path.join(DATA, 'test_seen.npy'), mmap_mode='r')
    meta = json.load(open(os.path.join(DATA, 'test_seen.json'), encoding='utf-8'))
    os.makedirs(OUT, exist_ok=True)
    expected = []
    for c, cls in enumerate(ts.CLASSES):
        best = None
        for k in range(len(meta)):
            if meta[k]['class'] != cls:
                continue
            if k % 7 != 3:                                                    # a sample, not all
                continue
            w = torch.tensor(np.asarray(clips[k]).astype(np.float32) / 32768).unsqueeze(0)
            with torch.no_grad():
                p = torch.softmax(model(w), dim=1)[0]
            if int(p.argmax()) == c and (best is None or float(p[c]) > best[0]):
                best = (float(p[c]), k, [float(v) for v in p])
        assert best is not None, cls
        name = f'{cls}.wav'
        sf.write(os.path.join(OUT, name), np.asarray(clips[best[1]]), ts.RATE, subtype='PCM_16')
        expected.append({'file': name, 'class': cls, 'probabilities': best[2], 'piece': meta[best[1]]['piece'], 'params': {k: v for k, v in meta[best[1]].items() if k not in ('piece', 'group')}})
        print(cls, best[0], meta[best[1]]['piece'])
    json.dump(expected, open(os.path.join(OUT, 'expected.json'), 'w', encoding='utf-8'), indent=1)


if __name__ == '__main__':
    main()
