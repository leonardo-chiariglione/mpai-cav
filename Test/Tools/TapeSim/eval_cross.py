"""Evaluates one or more trained TIC models on the test sets of one or more datasets (the first version on the second version's harder test sets, and back).
    python -I eval_cross.py model1:dataset1 model2:dataset2 ...   (each model is dataset/tic.pt under Test/Data/Music; test sets: every dataset given)
Run with the Python that has torch (Support/Python/pyannote-venv), always -I."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, torch
import train_tic as tt
import tape_sim as ts

MUSIC = os.path.join(tt.ROOT, 'Test', 'Data', 'Music')


def main():
    models = sys.argv[1:]
    for m in models:
        net = tt.Tic(); net.load_state_dict(torch.load(os.path.join(MUSIC, m, 'tic.pt'))); net.eval()
        for d in models:
            for name in ('test_seen', 'test_unseen'):
                clips = np.load(os.path.join(MUSIC, d, name + '.npy'), mmap_mode='r')
                meta = json.load(open(os.path.join(MUSIC, d, name + '.json'), encoding='utf-8'))
                index = {c: i for i, c in enumerate(ts.CLASSES)}
                truth = [index[x['class']] for x in meta]
                feats = tt.features(net, clips)
                pred = tt.predict(net.head, feats).argmax(1).tolist()
                s = tt.summarise(name, truth, pred, meta)
                rec = {c: round(v['recall'], 2) for c, v in s['per_class'].items()}
                print(f'model {m:12s} on {d:12s} {name:12s} accuracy {s["accuracy"]:.3f}  recall {rec}', flush=True)


if __name__ == '__main__':
    main()
