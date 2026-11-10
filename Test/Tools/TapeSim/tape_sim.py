"""Tape impairment simulator for the Tape Irregularity Classification (CAE-USC V2.5, CAE-TIC).

It takes music (Test/Data/Music/corpus: public domain / CC0, no speech) and applies the impairments an
open-reel tape gives, with the IrregularityType (CAE-IRR-V2.5, Tables 1 and 2 of the Irregularity File page)
known. THIS IS A SIMULATION, NOT TAPE: the model trained on it has never heard a real tape.

Classes (the audio-detectable IrregularityTypes, plus 'none' = not an irregularity, not selected):
  none  a clean clip (with or without hiss)
  da    damaged tape: a dropout shorter than 1 s, or clicks
  di    dirt: a burst of noise (crackle or hiss) on the music
  pps   play, pause and stop: a silence of 1 s or more
  ssv   speed standard variation: from the instant on, the tape plays at another speed (a ratio of two to a power)
  esv   equalisation standard variation: from the instant on, another equalisation (IEC1 <-> IEC2) without a change of speed
  sb    signal backward: from the instant on, the audio runs backwards
  wf    wow and flutter: the speed varies slowly (0.5-12 Hz) all along the clip
Not detectable in the audio: sp, b, sot, eot (the film), m, s (what the film shows).

Noise (hiss) is also added at random to every class, as a nuisance (SNR 22-50 dB); 'di' is the class of the noise
BURSTS. That mapping of noise to 'di' is a choice (the standard has no code for noise).

A clip is 4 s at 16 kHz mono. The Audio File of an irregularity in CAE-ARP is the irregularity with a margin of 1 s on each side; the classifier looks at
4 s centred on the middle of the irregularity, the missing samples by reflection (aap_view): the training clips are made that way too.

Parameter ranges: PARAMS['train'] are those the training set uses; PARAMS['unseen'] are outside them (used only
for the test, to see whether the classifier learned the impairment or its parameters).
"""
import cmath
import math
import os

import numpy as np
from scipy import signal

RATE = 16000
CLIP_SECONDS = 4.0
N = int(RATE * CLIP_SECONDS)
MID = N // 2
CLASSES = ['none', 'da', 'di', 'pps', 'ssv', 'esv', 'sb', 'wf']

# Time constants of the playback equalisation (microseconds): (low frequency, high frequency). The same table as
# AIMs/CAE1/V2.5/ARP/Tape.cs (from memory of IEC 60094, to be checked by someone who has it).
EQ = {('IEC1', 30): (0, 17.5), ('IEC1', 15): (0, 35), ('IEC1', 7.5): (0, 70), ('IEC1', 3.75): (3180, 90), ('IEC1', 1.875): (3180, 90), ('IEC1', 0.9375): (3180, 120),
      ('IEC2', 30): (0, 17.5), ('IEC2', 15): (3180, 50), ('IEC2', 7.5): (3180, 50), ('IEC2', 3.75): (3180, 90), ('IEC2', 1.875): (3180, 90), ('IEC2', 0.9375): (3180, 120)}

# TIC_HARD=1 (the second version of the classifier, night 2026-11-09): wow and flutter of more kinds (two components, a random speed walk, depths up to 2.5 %), and HARD NEGATIVES for
# 'none': a performance vibrato (4-7 Hz, 0.05-0.3 %) over part of the clip and a slow drift of pitch, which a clean clip of real music has and which must not be called wow and flutter.
HARD = os.environ.get('TIC_HARD') == '1'

PARAMS = {
    'train': {
        'dropout_ms': (20, 300), 'click_count': (1, 3), 'click_amp': (0.2, 0.9),
        'burst_ms': (50, 400), 'burst_snr_db': (-5, 10),
        'pps_s': (1.0, 2.0),
        'speed_octaves': (-2, -1, 1, 2),
        'eq_speeds': (7.5,), 'eq_jitter': 0.15,
        'wf_hz': (0.5, 6.0), 'wf_depth': (0.005, 0.025) if os.environ.get('TIC_HARD') == '1' else (0.005, 0.02),
    },
    'unseen': {
        'dropout_ms': (400, 800), 'click_count': (4, 6), 'click_amp': (0.05, 0.15),
        'burst_ms': (500, 900), 'burst_snr_db': (-15, -8),
        'pps_s': (2.2, 2.8),
        'speed_octaves': (-4, -3, 3, 4),
        'eq_speeds': (15,), 'eq_jitter': 0.15,
        'wf_hz': (8.0, 12.0), 'wf_depth': (0.025, 0.04),
    },
}


# ---------------------------------------------------------------------------------------------------------- audio
def load_piece(path, cache_dir=None):
    """The piece as mono int16 at 16 kHz (decoded with libsndfile; cached as .npy if cache_dir)."""
    import soundfile as sf
    if cache_dir:
        cached = os.path.join(cache_dir, os.path.splitext(os.path.basename(path))[0] + '.npy')
        if os.path.exists(cached):
            return np.load(cached)
    data, rate = sf.read(path, dtype='float32', always_2d=True)
    mono = data.mean(axis=1)
    if rate != RATE:
        g = math.gcd(rate, RATE)
        mono = signal.resample_poly(mono, RATE // g, rate // g).astype(np.float32)
    out = np.clip(np.round(mono * 32767), -32768, 32767).astype(np.int16)
    if cache_dir:
        os.makedirs(cache_dir, exist_ok=True)
        np.save(cached, out)
    return out


def pick_window(piece, rng, length, tries=50):
    """A start index so that piece[s:s+length] holds music (not silence)."""
    top = len(piece) - length
    if top <= 0:
        return None
    for _ in range(tries):
        s = int(rng.integers(0, top))
        w = piece[s:s + length].astype(np.float32) / 32768
        if np.sqrt(np.mean(w * w)) > 0.01 and np.sqrt(np.mean(w[: len(w) // 2] ** 2)) > 0.005 and np.sqrt(np.mean(w[len(w) // 2:] ** 2)) > 0.005:
            return s
    return None


def dropout_gain(n, start, length, floor, fade):
    """A gain of 1 with a hole of `length` samples at `start` where it is `floor`, entered and left by a ramp of `fade` samples."""
    g = np.ones(n, np.float32)
    end = min(n, start + length)
    start = max(0, start)
    g[start:end] = floor
    lo = max(0, start - fade)
    if start - lo > 0:
        g[lo:start] = np.linspace(1, floor, start - lo, endpoint=False, dtype=np.float32)
    hi = min(n, end + fade)
    if hi - end > 0:
        g[end:hi] = np.linspace(floor, 1, hi - end, endpoint=False, dtype=np.float32)
    return g


# ---------------------------------------------------------------------------------------------------------- impairments
def eq_response(freqs, standard, speed, jitter=None):
    lf, hf = EQ[(standard, speed)]
    if jitter:
        lf = lf * jitter[0]
        hf = hf * jitter[1]
    jw = 1j * 2 * np.pi * np.maximum(freqs, 1.0)
    h = 1 / (1 + jw * hf * 1e-6)
    if lf > 0:
        h = (1 + jw * lf * 1e-6) / (jw * lf * 1e-6) * h
    return h


def apply_eq_change(x, read_std, write_std, speed, rng, jitter):
    """The audio of a tape written with write_std and played with read_std at one speed: the spectrum is multiplied by the
    response of the playback equalisation that was used over the one that should have been (as Tape.CorrectEqualisation
    undoes it), normalised at 1 kHz."""
    n = int(2 ** math.ceil(math.log2(len(x) + 1)))
    f = np.fft.rfftfreq(n, 1 / RATE)
    j1 = (1 + rng.uniform(-jitter, jitter), 1 + rng.uniform(-jitter, jitter))
    j2 = (1 + rng.uniform(-jitter, jitter), 1 + rng.uniform(-jitter, jitter))
    h = eq_response(f, read_std, speed, j1) / eq_response(f, write_std, speed, j2)
    h /= abs(h[np.argmin(np.abs(f - 1000))])
    y = np.fft.irfft(np.fft.rfft(x, n) * h, n)[:len(x)]
    return y.astype(np.float32)


def speed_change(source, f):
    """source resampled to play f times faster (pitch up by f); f is a power of two or a ratio of small integers."""
    from fractions import Fraction
    fr = Fraction(1.0 / f).limit_denominator(64)
    return signal.resample_poly(source, fr.numerator, fr.denominator).astype(np.float32)


def wow_flutter(x, rate_hz, depth, phase):
    """Reads x at a speed 1 + depth*sin(2 pi rate t): positions are the integral of the speed."""
    t = np.arange(len(x)) / RATE
    pos = (t + depth / (2 * np.pi * rate_hz) * (1 - np.cos(2 * np.pi * rate_hz * t + phase) + np.cos(phase))) * RATE
    return np.interp(pos, np.arange(len(x)), x).astype(np.float32)


def hiss(n, snr_db, rng, ref_rms):
    noise = rng.standard_normal(n).astype(np.float32)
    b, a = signal.butter(1, 400 / (RATE / 2), 'high')                 # tape hiss is mostly high-frequency
    noise = signal.lfilter(b, a, noise).astype(np.float32)
    noise *= ref_rms / (10 ** (snr_db / 20)) / max(np.sqrt(np.mean(noise ** 2)), 1e-9)
    return noise


def reflect_index(i, n):
    """i folded into 0..n-1 by reflection about the ends (the same in the C# of the AIM)."""
    if n == 1:
        return 0
    period = 2 * (n - 1)
    i = i % period
    return period - i if i >= n else i


def aap_view(y, event):
    """What CAE-TIC gets for an irregularity: the Audio File of Audio Analysis for Preservation, the irregularity with a margin of 1 s on each
    side (clamped to the file), seen through a window of 4 s centred on the middle of the irregularity, the missing samples by reflection."""
    t0, t1 = event
    c0 = max(0, t0 - RATE)
    c1 = min(len(y), t1 + RATE)
    seg = y[c0:c1]
    centre = (t0 + t1) // 2 - c0
    idx = np.array([reflect_index(i, len(seg)) for i in range(centre - MID, centre - MID + N)])
    return seg[idx]


def make_clip(piece, cls, rng, which='train', want_params=True):
    """One clip of the class from the piece (int16 at 16 kHz): returns (int16 clip of N samples, params dict) or None."""
    P = PARAMS[which]
    params = {'class': cls}
    # the source needs more than N for an increase of speed in the second half
    octave = None
    if cls == 'ssv':
        octave = int(rng.choice(P['speed_octaves']))
        params['octaves'] = octave
        need = MID + int(MID * 2.0 ** octave) + 64
    else:
        need = N + 64
    s = pick_window(piece, rng, max(need, N))
    if s is None:
        return None
    x = piece[s:s + max(need, N)].astype(np.float32) / 32768
    first = x[:MID].copy()
    level = 10 ** (rng.uniform(-22, -3) / 20)                          # peak level -22..-3 dBFS
    peak = max(np.max(np.abs(x[:N])), 1e-6)
    x = x * (level / peak)
    first = x[:MID].copy()
    y = x[:N].copy()

    if cls == 'ssv':
        f = 2.0 ** octave
        second = speed_change(x[MID:], f)[:MID]
        if len(second) < MID:
            return None
        y = np.concatenate([first, second])
        params['ratio'] = f
        event = (MID, MID)
    elif cls == 'esv':
        speed = float(rng.choice(P['eq_speeds']))
        read_std, write_std = ('IEC1', 'IEC2') if rng.random() < 0.5 else ('IEC2', 'IEC1')
        second = apply_eq_change(x[MID:N], read_std, write_std, speed, rng, P['eq_jitter'])
        if rng.random() < 0.5:                                          # keep the level (a level step is a cue the real tape would also give; half of the clips have it)
            second *= np.sqrt(np.mean(x[MID:N] ** 2) + 1e-12) / np.sqrt(np.mean(second ** 2) + 1e-12)
        y = np.concatenate([first, second])
        params.update(speed=speed, read=read_std, write=write_std)
        event = (MID, MID)
    elif cls == 'sb':
        y = np.concatenate([first, x[MID:N][::-1].copy()])
        event = (MID, MID)
    elif cls == 'wf':
        hz = float(rng.uniform(*P['wf_hz']))
        depth = float(rng.uniform(*P['wf_depth']))
        y = wow_flutter(x[:N], hz, depth, float(rng.uniform(0, 2 * np.pi)))
        if HARD:
            mode = rng.random()
            if mode < 0.25:                                             # a second, faster component (flutter on top of wow)
                y = wow_flutter(y, float(rng.uniform(6, 30)), depth * float(rng.uniform(0.2, 0.6)), float(rng.uniform(0, 2 * np.pi)))
                params['mode'] = 'two'
            elif mode < 0.5:                                            # a random walk of the speed (not a sine)
                t = np.arange(N) / RATE
                walk = signal.lfilter(*signal.butter(2, hz / (RATE / 2)), rng.standard_normal(N)).astype(np.float64)
                walk = walk / max(np.max(np.abs(walk)), 1e-9) * depth
                pos = (t + np.cumsum(walk) / RATE) * RATE
                y = np.interp(pos, np.arange(N), x[:N]).astype(np.float32)
                params['mode'] = 'walk'
        params.update(hz=hz, depth=depth)
        event = (0, N)
    elif cls == 'da':
        if rng.random() < 0.5:                                          # a dropout
            ms = float(rng.uniform(*P['dropout_ms']))
            length = int(ms * RATE / 1000)
            start = MID - length // 2 + int(rng.integers(-1600, 1600))
            floor = float(rng.choice([0.0, 0.0, 0.02, 0.05]))
            gain = dropout_gain(len(y), start, length, floor, 32)
            y = y * gain
            params.update(kind='dropout', ms=ms, floor=floor)
            event = (start, start + length)
        else:                                                           # clicks
            count = int(rng.integers(P['click_count'][0], P['click_count'][1] + 1))
            amp = float(rng.uniform(*P['click_amp']))
            lo, hi = N, 0
            for _ in range(count):
                pos = MID + int(rng.integers(-2400, 2400))
                width = int(rng.integers(1, 4))
                y[pos:pos + width] += float(rng.choice([-1, 1])) * amp
                lo, hi = min(lo, pos), max(hi, pos + width)
            params.update(kind='click', count=count, amp=amp)
            event = (lo, hi)
    elif cls == 'di':
        ms = float(rng.uniform(*P['burst_ms']))
        length = int(ms * RATE / 1000)
        start = MID - length // 2 + int(rng.integers(-1600, 1600))
        snr = float(rng.uniform(*P['burst_snr_db']))
        ref = float(np.sqrt(np.mean(y ** 2)) + 1e-9)
        if rng.random() < 0.5:                                          # a burst of broadband noise
            burst = rng.standard_normal(length).astype(np.float32)
        else:                                                           # crackle: many small impulses
            burst = (rng.random(length) < 0.02) * rng.standard_normal(length).astype(np.float32)
            burst = signal.lfilter([1, -0.6], [1], burst).astype(np.float32)
        burst *= ref / (10 ** (snr / 20)) / max(np.sqrt(np.mean(burst ** 2)), 1e-9)
        win = np.hanning(length).astype(np.float32) ** 0.3
        y[start:start + length] += burst * win
        params.update(ms=ms, snr=snr)
        event = (start, start + length)
    elif cls == 'pps':
        sec = float(rng.uniform(*P['pps_s']))
        length = int(sec * RATE)
        start = MID - length // 2 + int(rng.integers(-1600, 1600))
        start = max(start, 0)
        gain = dropout_gain(len(y), start, length, 0.0, 80)
        y = y * gain
        params.update(seconds=sec)
        event = (start, start + length)
    elif cls == 'none':
        if HARD and rng.random() < 0.6:
            span = int(rng.uniform(1.0, 3.0) * RATE); a0 = int(rng.integers(0, N - span))
            vib = wow_flutter(x[:N], float(rng.uniform(4, 7)), float(rng.uniform(0.0005, 0.003)), float(rng.uniform(0, 2 * np.pi)))
            if rng.random() < 0.5:                                      # a slow drift of pitch as well
                vib = wow_flutter(vib, float(rng.uniform(0.2, 0.8)), float(rng.uniform(0.0005, 0.002)), float(rng.uniform(0, 2 * np.pi)))
            w = np.zeros(N, np.float32); w[a0:a0 + span] = np.hanning(span)
            y = (1 - w) * x[:N] + w * vib
            params['hard'] = 'vibrato'
        event = (0, N)
    else:
        raise ValueError(cls)

    if rng.random() < 0.6:                                              # the hiss of the tape, under everything
        snr = float(rng.uniform(22, 50))
        y = y + hiss(len(y), snr, rng, float(np.sqrt(np.mean(first ** 2)) + 1e-9))
        params['hiss_snr'] = snr
    y = np.clip(y, -1, 1)
    out = np.round(y * 32767).astype(np.int16)
    params['event'] = [int(event[0]), int(event[1])]
    return aap_view(out, event), params


# ---------------------------------------------------------------------------------------------------------- the dataset split
def split_by_piece(files_by_group, seed=7):
    """Test pieces (every fifth piece of a group, at least one), validation pieces (one more per group where the group has at
    least four), the rest for training. A piece belongs to ONE set; all its clips go there."""
    rng = np.random.default_rng(seed)
    split = {}
    for group, files in sorted(files_by_group.items()):
        files = sorted(files)
        order = list(rng.permutation(len(files)))
        n_test = max(1, round(len(files) / 5))
        n_val = 1 if len(files) >= 4 else 0
        for rank, i in enumerate(order):
            split[files[i]] = 'test' if rank < n_test else 'val' if rank < n_test + n_val else 'train'
    return split
