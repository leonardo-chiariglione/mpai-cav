# Tape impairment simulator and Tape Irregularity Classifier (CAE-USC V2.5, CAE-TIC)

**The classifier is trained on SIMULATED impairments of music, not on real tapes.** The simulator makes the impairments with a model of its own (the
equalisation constants are the ones of `AIMs/CAE1/V2.5/ARP/Tape.cs`, from memory of IEC 60094 and to be checked); a result on the simulator says
how well the model learned that simulation, not how it would do on a tape.

| File | What it does |
|---|---|
| `tape_sim.py` | the impairments (dropout, click, noise burst, silence, speed change, equalisation change, signal backward, wow and flutter, hiss), the split of the pieces, the view the AIM has of an irregularity |
| `make_dataset.py` | decodes `Test/Data/Music/corpus` (78 public-domain / CC0 pieces, no speech), splits the PIECES into train / val / test, writes `Test/Data/Music/dataset/*.npy` |
| `train_tic.py` | the network (log-mel front end inside the ONNX graph), the training, the report (`tic-report.json`), the export `TapeIrregularityClassifier.onnx` |
| `make_golden.py` | eight clips (one per class) with the probabilities PyTorch gives, for the C# test (`Test/Data/Music/tic-golden`) |

Run with a Python that has numpy, scipy, soundfile and torch (here `Support/Python/pyannote-venv`), always `-I`:

    python -I make_dataset.py
    python -I train_tic.py --epochs 40
    python -I make_golden.py

The ONNX file is then put where the L3 of the AIM names it (`AccessSources` of `AIMs/AMDs/1CAE-TIC-V2.5-I01.json`, Setting `TicModel`) with its SHA-256.

## Classes

The audio-detectable IrregularityTypes of the Irregularity File: `da` (dropout shorter than 1 s, clicks), `di` (noise bursts), `pps` (silence of 1 s or more), `ssv` (speed standard
variation), `esv` (equalisation standard variation), `sb` (signal backward), `wf` (wow and flutter), and `none` (not an irregularity: not selected). Not detectable in the audio: `sp`, `b`,
`sot`, `eot` (what the film shows), `m`, `s`. Noise has no code in the standard: the bursts of noise are given `di` (a choice).

## Splits

By piece, never by clip: a piece is in exactly one of train, val, test (`dataset/split.json`). `test_seen` has impairment parameters from the training ranges; `test_unseen`
has parameters outside them (dropouts and silences longer, clicks quieter, speed ratios of 8 and 16, the equalisation pair of 15 ips, wow and flutter faster and deeper).
