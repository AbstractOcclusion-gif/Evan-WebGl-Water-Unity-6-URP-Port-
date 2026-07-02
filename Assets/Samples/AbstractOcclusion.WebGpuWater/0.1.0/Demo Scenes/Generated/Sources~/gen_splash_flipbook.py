"""Generates Generated/SplashFlipbook_8x8.png: a 64-frame crown-splash flipbook,
128px per frame in an 8x8 grid (read left-to-right, top-to-bottom), RGBA with
the splash shape in alpha. Plays once over a particle's lifetime (see
WaterSplashEmitter.ConfigureCrown) and ends on empty frames so the particle
vanishes cleanly.

Built from a tiny ballistic particle sim viewed as a billboard projection:
- a dense "sheet" of fine droplets spawned on a ring forms the crown curtain
  (azimuthal lobes give the classic crown rim),
- fewer, larger rim droplets detach later with velocity-stretched stamps,
- back-of-ring particles are dimmed for depth,
- a soft-knee tonemap keeps mid-life frames readable, and a floor cut removes
  background speckle.

Requires scipy.  Run:  python3 gen_splash_flipbook.py
"""
import os
import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

FRAME_SIZE = 128
COLS, ROWS = 8, 8
FRAME_COUNT = COLS * ROWS
DURATION = 0.8          # seconds of simulated splash across the sequence
GRAVITY = 5.0           # world units / s^2
WORLD_HEIGHT = 1.6      # world y span mapped to the frame height
SEED = 11
SHEET_COUNT = 4200      # fine curtain droplets
DROPLET_COUNT = 460     # large detaching rim droplets
SPECKLE_FLOOR = 0.02    # subtracted before the tonemap: kills background dust
TONEMAP_KNEE = 0.30
END_FADE_START = 0.75   # fraction of the sequence where the global fade-out begins
TINT = (242, 250, 255)  # cool white
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "SplashFlipbook_8x8.png")

rng = np.random.default_rng(SEED)

sheet_az = rng.uniform(0, 2 * np.pi, SHEET_COUNT)
sheet_r0 = rng.normal(0.14, 0.02, SHEET_COUNT)
sheet_vup = rng.normal(2.6, 0.50, SHEET_COUNT) * \
    (0.75 + 0.25 * np.cos(2 * sheet_az + rng.normal(0, 0.8, SHEET_COUNT)))  # crown lobes
sheet_vout = rng.normal(1.35, 0.35, SHEET_COUNT)
sheet_birth = rng.uniform(0.0, 0.06, SHEET_COUNT)
sheet_size = rng.uniform(0.6, 1.4, SHEET_COUNT)

drop_az = rng.uniform(0, 2 * np.pi, DROPLET_COUNT)
drop_r0 = rng.normal(0.16, 0.03, DROPLET_COUNT)
drop_vup = rng.normal(3.3, 0.6, DROPLET_COUNT)
drop_vout = rng.normal(1.7, 0.5, DROPLET_COUNT)
drop_birth = rng.uniform(0.04, 0.22, DROPLET_COUNT)
drop_size = rng.uniform(1.6, 3.0, DROPLET_COUNT)


def project(az, r0, vout, vup, birth, t):
    """Ballistic ring particles projected onto the billboard plane."""
    age = np.maximum(t - birth, 0.0)
    alive = t >= birth
    x = np.sin(az) * (r0 + vout * age)
    depth = 0.55 + 0.45 * np.cos(az)  # back of the ring dimmer
    y = vup * age - 0.5 * GRAVITY * age * age
    return x, y, depth, age, alive


def stamp(img, xs, ys, weights, sigma):
    """Additive gaussian splat: histogram the points, then blur once per size class."""
    h, w = img.shape
    px = ((xs + 1.0) * 0.5 * (w - 1)).astype(int)
    py = ((1.0 - ys / WORLD_HEIGHT) * (h - 1)).astype(int)
    ok = (px >= 0) & (px < w) & (py >= 0) & (py < h)
    acc = np.zeros_like(img)
    np.add.at(acc, (py[ok], px[ok]), weights[ok])
    img += gaussian_filter(acc, sigma)


frames = []
for i in range(FRAME_COUNT):
    t = (i + 0.5) / FRAME_COUNT * DURATION
    img = np.zeros((FRAME_SIZE, FRAME_SIZE))

    x, y, depth, age, alive = project(sheet_az, sheet_r0, sheet_vout, sheet_vup, sheet_birth, t)
    fade = np.clip(1.15 - age / DURATION, 0, 1) ** 0.8
    weight = depth * fade * alive * (y > -0.02)
    for size_class, sigma in ((sheet_size < 1.0, 1.0), (sheet_size >= 1.0, 1.7)):
        stamp(img, x[size_class], y[size_class], (weight * sheet_size)[size_class], sigma)

    xd, yd, dd, aged, alived = project(drop_az, drop_r0, drop_vout, drop_vup, drop_birth, t)
    vy = drop_vup - GRAVITY * aged
    faded = np.clip(1.2 - aged / DURATION, 0, 1) ** 0.8
    wd = dd * faded * alived * (yd > -0.02)
    for k in (-1, 0, 1):  # velocity stretch: 3 sub-stamps along the motion direction
        dt = k * 0.014
        stamp(img, xd + np.sin(drop_az) * drop_vout * dt, yd + vy * dt, wd * drop_size * 0.5, 2.0)

    frac = (i + 0.5) / FRAME_COUNT
    envelope = min(t / 0.04, 1.0) * \
        (1.0 - np.clip((frac - END_FADE_START) / (1.0 - END_FADE_START), 0, 1) ** 1.3)
    frames.append(img * envelope)

stack = np.array(frames)
stack = np.maximum(stack - SPECKLE_FLOOR, 0.0)
stack = stack / (stack + TONEMAP_KNEE)                     # soft knee lifts mid-life frames
stack = np.clip(stack / np.percentile(stack, 99.9), 0, 1)

sheet = np.zeros((FRAME_SIZE * ROWS, FRAME_SIZE * COLS))
for i, frame in enumerate(stack):
    r, c = divmod(i, COLS)
    sheet[r * FRAME_SIZE:(r + 1) * FRAME_SIZE, c * FRAME_SIZE:(c + 1) * FRAME_SIZE] = frame

alpha = (sheet * 255).astype(np.uint8)
rgba = np.dstack([(sheet * TINT[0]).astype(np.uint8),
                  (sheet * TINT[1]).astype(np.uint8),
                  (sheet * TINT[2]).astype(np.uint8), alpha])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(OUTPUT))
print("wrote", os.path.abspath(OUTPUT))
