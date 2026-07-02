"""Generates Generated/FoamFlipbook_4x4.png: 16 frames of surface-foam pattern,
256px each, laid out in a 4x4 grid (read left-to-right, top-to-bottom).

Guarantees the two properties the WaterSurface shader relies on:
- each frame tiles seamlessly in x/y (FFT synthesis is periodic by construction),
- the 16-frame sequence loops seamlessly in time (every spectral coefficient's
  phase advances an integer number of full cycles across the loop).

Look: two octaves of ridged lace (bright filaments along noise zero-crossings)
gated by a slow "breathing" density field, plus fine fizz. All frames share one
normalization so playback doesn't flicker.

Run:  python3 gen_foam_flipbook.py  (writes into the parent Generated/ folder)
"""
import os
import numpy as np
from PIL import Image

FRAME_SIZE = 256
COLS, ROWS = 4, 4
FRAME_COUNT = COLS * ROWS
SEED = 7
MIN_DENSITY = 0.35  # foam never goes fully dead-black inside a foamy region
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "FoamFlipbook_4x4.png")

rng = np.random.default_rng(SEED)
ky, kx = np.meshgrid(np.fft.fftfreq(FRAME_SIZE) * FRAME_SIZE,
                     np.fft.fftfreq(FRAME_SIZE) * FRAME_SIZE, indexing="ij")
kmag = np.hypot(kx, ky)
kmag[0, 0] = 1.0


def band(power, kmin, kmax, max_cycles):
    """Random time-looping spectral field, amplitude ~ 1/k^power inside [kmin, kmax]."""
    amp = np.where((kmag >= kmin) & (kmag <= kmax), kmag ** -power, 0.0)
    phase = rng.uniform(0, 2 * np.pi, (FRAME_SIZE, FRAME_SIZE))
    cycles = rng.integers(-max_cycles, max_cycles + 1, (FRAME_SIZE, FRAME_SIZE))
    return lambda t: np.real(np.fft.ifft2(amp * np.exp(1j * (phase + 2 * np.pi * cycles * t))))


def norm(field):
    return field / (np.std(field) + 1e-9)


clumps = band(power=2.2, kmin=1, kmax=6, max_cycles=1)    # slow density breathing
webs = band(power=1.5, kmin=5, kmax=34, max_cycles=2)     # main lace octave
webs2 = band(power=1.5, kmin=10, kmax=60, max_cycles=2)   # finer lace octave
fizz = band(power=1.1, kmin=40, kmax=110, max_cycles=3)   # sparkle churn

frames = []
for i in range(FRAME_COUNT):
    t = i / FRAME_COUNT
    c, w, w2, z = norm(clumps(t)), norm(webs(t)), norm(webs2(t)), norm(fizz(t))
    ridge = np.clip(1.0 - np.abs(w) / 2.2, 0, 1) ** 2.5
    ridge2 = np.clip(1.0 - np.abs(w2) / 2.2, 0, 1) ** 2.5
    lace = np.clip(ridge * 0.75 + ridge2 * 0.55, 0, 1)
    density = np.clip(0.72 + 0.38 * c, MIN_DENSITY, 1.0)
    frames.append(lace * density + 0.16 * np.clip(z, 0, None))

stack = np.array(frames)
lo, hi = np.percentile(stack, 1), np.percentile(stack, 99.5)
stack = np.clip((stack - lo) / (hi - lo), 0, 1) ** 1.1  # shared scale: no flicker

sheet = np.zeros((FRAME_SIZE * ROWS, FRAME_SIZE * COLS))
for i, frame in enumerate(stack):
    r, c = divmod(i, COLS)
    sheet[r * FRAME_SIZE:(r + 1) * FRAME_SIZE, c * FRAME_SIZE:(c + 1) * FRAME_SIZE] = frame

gray = (sheet * 255).astype(np.uint8)
rgba = np.dstack([gray, gray, gray, np.full_like(gray, 255)])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(OUTPUT))
print("wrote", os.path.abspath(OUTPUT))
