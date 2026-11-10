"""Builds the dataset of simulated tape impairments of music for the Tape Irregularity Classification.

  python -I make_dataset.py  [--python-from a venv that has numpy, scipy, soundfile]

Reads Test/Data/Music/corpus (manifest.json), decodes every piece to mono 16 kHz (cache: Test/Data/Music/cache16k),
splits THE PIECES (never the clips) into train / val / test, and writes to Test/Data/Music/dataset/:

  train.npy, val.npy, test_seen.npy, test_unseen.npy   int16 [clips, 64000]  (4 s at 16 kHz)
  <set>.json                                            per clip: class, piece, group, parameters
  split.json                                            the piece of every set

test_seen has impairment parameters from the same ranges as training; test_unseen from ranges outside them (tape_sim.PARAMS).
"""
import json
import os
import sys
import time
from multiprocessing import Pool

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import tape_sim as ts  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
MUSIC = os.path.join(ROOT, 'Test', 'Data', 'Music')
CORPUS = os.path.join(MUSIC, 'corpus')
CACHE = os.path.join(MUSIC, 'cache16k')
OUT = os.path.join(MUSIC, os.environ.get('TIC_DATASET', 'dataset'))
PER_CLASS = {'train': 24, 'val': 24, 'test_seen': 20, 'test_unseen': 20}


def job(args):
    relpath, group, which, set_name, per_class, seed = args
    piece = ts.load_piece(os.path.join(CORPUS, relpath), CACHE)
    rng = np.random.default_rng(seed)
    clips, meta = [], []
    for cls in ts.CLASSES:
        made, attempts = 0, 0
        while made < per_class and attempts < per_class * 6:
            attempts += 1
            r = ts.make_clip(piece, cls, rng, which)
            if r is None:
                continue
            clips.append(r[0])
            m = dict(r[1]); m['piece'] = relpath; m['group'] = group
            meta.append(m)
            made += 1
    return set_name, (np.stack(clips) if clips else np.zeros((0, ts.N), np.int16)), meta


def main():
    manifest = json.load(open(os.path.join(CORPUS, 'manifest.json'), encoding='utf-8'))['files']
    by_group = {}
    for f in manifest:
        by_group.setdefault(f['group'], []).append(f['file'])
    split = ts.split_by_piece(by_group)
    os.makedirs(OUT, exist_ok=True)
    json.dump({'train': sorted(k for k, v in split.items() if v == 'train'), 'val': sorted(k for k, v in split.items() if v == 'val'), 'test': sorted(k for k, v in split.items() if v == 'test')},
              open(os.path.join(OUT, 'split.json'), 'w', encoding='utf-8'), indent=1)
    group_of = {f['file']: f['group'] for f in manifest}
    jobs = []
    for i, (piece, which_set) in enumerate(sorted(split.items())):
        if which_set == 'train':
            jobs.append((piece, group_of[piece], 'train', 'train', PER_CLASS['train'], 1000 + i))
        elif which_set == 'val':
            jobs.append((piece, group_of[piece], 'train', 'val', PER_CLASS['val'], 2000 + i))
        else:
            jobs.append((piece, group_of[piece], 'train', 'test_seen', PER_CLASS['test_seen'], 3000 + i))
            jobs.append((piece, group_of[piece], 'unseen', 'test_unseen', PER_CLASS['test_unseen'], 4000 + i))
    print(f'{len(manifest)} pieces: ' + ', '.join(f'{s} {sum(1 for v in split.values() if v == s)}' for s in ('train', 'val', 'test')), flush=True)
    t0 = time.time()
    parts = {k: ([], []) for k in PER_CLASS}
    with Pool(max(1, (os.cpu_count() or 4) - 2)) as pool:
        for n, (name, clips, meta) in enumerate(pool.imap_unordered(job, jobs), 1):
            parts[name][0].append(clips)
            parts[name][1].extend(meta)
            if n % 10 == 0 or n == len(jobs):
                print(f'  {n}/{len(jobs)} jobs, {time.time() - t0:.0f} s', flush=True)
    for name, (arrays, meta) in parts.items():
        a = np.concatenate(arrays)
        np.save(os.path.join(OUT, name + '.npy'), a)
        json.dump(meta, open(os.path.join(OUT, name + '.json'), 'w', encoding='utf-8'))
        counts = {c: sum(1 for m in meta if m['class'] == c) for c in ts.CLASSES}
        print(name, a.shape, counts, flush=True)


if __name__ == '__main__':
    main()
