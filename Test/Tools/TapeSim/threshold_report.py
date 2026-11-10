"""Adds to tic-report.json what a threshold on the probability does: for each test set, the share of clips the model answers with a probability of at least
the threshold, and its accuracy on those. (The AIM gives no type under its MinProbability: the rules decide.)   <venv python> -I threshold_report.py"""
import json, os, sys
import numpy as np, torch
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import tape_sim as ts, train_tic as tt
DATA = os.path.join(os.path.abspath(os.path.join(HERE, '..', '..', '..')), 'Test', 'Data', 'Music', 'dataset')
model = tt.Tic(); model.load_state_dict(torch.load(os.path.join(DATA, 'tic.pt'))); model.eval()
report = json.load(open(os.path.join(DATA, 'tic-report.json'), encoding='utf-8'))
index = {c: i for i, c in enumerate(ts.CLASSES)}
out = {}
for name in ('test_seen', 'test_unseen'):
    clips = np.load(os.path.join(DATA, name + '.npy'), mmap_mode='r'); meta = json.load(open(os.path.join(DATA, name + '.json'), encoding='utf-8'))
    y = np.array([index[m['class']] for m in meta])
    feats = tt.features(model, np.asarray(clips)); p = torch.softmax(tt.predict(model.head, feats), 1).numpy()
    rows = {}
    for t in (0.5, 0.8, 0.9):
        keep = p.max(1) >= t
        rows[str(t)] = {'answered': float(keep.mean()), 'accuracy_on_answered': float((p.argmax(1)[keep] == y[keep]).mean())}
    out[name] = rows
report['probability_threshold'] = out
json.dump(report, open(os.path.join(DATA, 'tic-report.json'), 'w', encoding='utf-8'), indent=1)
print(json.dumps(out, indent=1))
