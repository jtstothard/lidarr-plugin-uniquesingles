"""Deterministic original synthetic PCM; fixture encoder used: FFmpeg 9.0.2 (not needed at test runtime)."""
import pathlib
import struct
import subprocess

ROOT = pathlib.Path(__file__).parent
samples = [((i * 313) % 60001) - 30000 for i in range(12000)]


def encode(name, values=samples, rate=8000, channels=1, bits=16, compression=0, tag="one"):
    raw = b"".join((v if bits == 16 else v * 256 + 7).to_bytes(bits // 8, 'little', signed=True) for v in values)
    subprocess.run(['ffmpeg', '-v', 'error', '-f', f's{bits}le', '-ar', str(rate), '-ac', str(channels),
                    '-i', 'pipe:0', '-c:a', 'flac', '-compression_level', str(compression), '-metadata',
                    'title=' + tag, '-y', str(ROOT / name)], input=raw, check=True)


encode('original.flac')
encode('same-tags.flac', compression=12, tag='different tags')
encode('late-sample.flac', values=samples[:-1] + [samples[-1] + 1])
encode('different-count.flac', values=samples[:-1])
encode('different-rate.flac', rate=16000)
encode('different-depth.flac', bits=24)
encode('24-tags.flac', bits=24, compression=12, tag='24-bit tags')
encode('stereo.flac', values=[x for s in samples for x in (s, -s)], channels=2)
original = (ROOT / 'original.flac').read_bytes()
changed = bytearray((ROOT / 'late-sample.flac').read_bytes())
changed[26:42] = original[26:42]  # Valid differing audio with forged matching stored MD5.
(ROOT / 'forged-md5.flac').write_bytes(changed)
zero = bytearray(original)
zero[26:42] = bytes(16)
(ROOT / 'zero-md5.flac').write_bytes(zero)
corrupt = bytearray(original)
corrupt[-20] ^= 0x40
(ROOT / 'corrupt.flac').write_bytes(corrupt)
(ROOT / 'truncated.flac').write_bytes(original[:-100])
lying = bytearray((ROOT / 'different-rate.flac').read_bytes())
packed = int.from_bytes(lying[18:26], 'big')
lying[18:26] = ((packed & ((1 << 44) - 1)) | (8000 << 44)).to_bytes(8, 'big')
(ROOT / 'lying-rate.flac').write_bytes(lying)
