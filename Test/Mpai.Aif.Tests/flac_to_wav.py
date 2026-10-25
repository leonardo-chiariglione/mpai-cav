# Reads LibriSpeech FLAC files out of a .tar.gz and writes them as WAV (16-bit mono), the first N utterances of each speaker in the
# order of the archive, so that C# code, which cannot decode FLAC, can embed them. Used by BuildReferenceVoices.
#   python flac_to_wav.py <dev-clean.tar.gz> <output folder> <utterances per speaker>
import io, os, sys, tarfile
import numpy as np
import soundfile as sf

archive, out, per = sys.argv[1], sys.argv[2], int(sys.argv[3])
os.makedirs(out, exist_ok=True)
taken = {}
with tarfile.open(archive, 'r|gz') as tar:
    for m in tar:
        if not (m.isfile() and m.name.endswith('.flac')):
            continue
        speaker = m.name.split('/')[-3]
        if taken.get(speaker, 0) >= per:
            continue
        data, rate = sf.read(io.BytesIO(tar.extractfile(m).read()), dtype='int16')
        if data.ndim > 1:
            data = data.mean(axis=1).astype(np.int16)
        folder = os.path.join(out, speaker)
        os.makedirs(folder, exist_ok=True)
        sf.write(os.path.join(folder, os.path.basename(m.name)[:-5] + '.wav'), data, rate, subtype='PCM_16')
        taken[speaker] = taken.get(speaker, 0) + 1
print(len(taken), 'speakers', sum(taken.values()), 'utterances')
