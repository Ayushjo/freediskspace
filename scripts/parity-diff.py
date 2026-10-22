#!/usr/bin/env python3
"""Compare Mac and Windows renders of the same page, for the Windows parity work.

  python3 scripts/parity-diff.py MAC_DIR WIN_DIR OUT_DIR [--keys overview,cleanSafe]

MAC_DIR holds the macOS snapshot harness output (<key>-<light|dark>.png); WIN_DIR holds
windows/scripts/render-parity.ps1 output (<pagekey>-<light|dark>.png). For every page in
both, OUT_DIR gets <key>-<theme>.png: Mac | Windows | difference heat map, plus report.md
with a score per page (share of pixels that differ by more than a small tolerance, and the
mean difference). Text never matches exactly (SF on Mac, Segoe on Windows); the score is
for layout, colour and spacing, the heat map shows where.
Needs Pillow and numpy.
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

# Mac harness key -> Windows page key (lowercased page name without spaces).
KEYS = {
    "overview": "overview", "find": "find", "biggestFiles": "biggestfiles",
    "biggestFolders": "biggestfolders", "forgottenFiles": "forgottenfiles",
    "duplicates": "duplicates", "cleanSafe": "safetoreview", "cleanCaches": "caches",
    "cleanDownloads": "olddownloads", "cleanMedia": "largemedia", "fileBrowser": "filebrowser",
    "visualize": "visualize", "developerStorage": "developerstorage",
    "applications": "applications", "snapshots": "snapshots", "history": "history",
}
TOLERANCE = 24  # per-channel difference ignored as antialiasing / font rendering noise


def load(path, size):
    image = Image.open(path).convert("RGB")
    return image if image.size == size else image.resize(size, Image.LANCZOS)


def compare(mac_path, win_path, out_path):
    mac = Image.open(mac_path).convert("RGB")
    win = load(win_path, mac.size)
    a = np.asarray(mac, dtype=np.int16)
    b = np.asarray(win, dtype=np.int16)
    delta = np.abs(a - b).max(axis=2)
    differs = delta > TOLERANCE
    share = float(differs.mean())
    mean = float(delta.mean())
    heat = np.zeros_like(a, dtype=np.uint8)
    heat[..., 0] = np.clip(delta * 2, 0, 255)
    heat[..., 1] = np.where(differs, 40, 0)
    base = (np.asarray(mac.convert("L"), dtype=np.uint8) // 4)[..., None].repeat(3, axis=2)
    overlay = np.maximum(base, heat)
    w, h = mac.size
    sheet = Image.new("RGB", (w * 3, h + 60), "white")
    for i, (label, image) in enumerate((("Mac", mac), ("Windows", win), ("difference", Image.fromarray(overlay)))):
        sheet.paste(image, (i * w, 60))
        ImageDraw.Draw(sheet).text((i * w + 20, 20), label, fill="black")
    sheet.thumbnail((3 * 1440, 1000))
    sheet.save(out_path, quality=85)
    return share, mean


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mac")
    parser.add_argument("win")
    parser.add_argument("out")
    parser.add_argument("--keys", default="")
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    wanted = [k for k in args.keys.split(",") if k] or list(KEYS)
    rows = []
    for key in wanted:
        for theme in ("light", "dark"):
            mac_path = os.path.join(args.mac, f"{key}-{theme}.png")
            win_path = os.path.join(args.win, f"{KEYS.get(key, key.lower())}-{theme}.png")
            if not (os.path.exists(mac_path) and os.path.exists(win_path)):
                rows.append((key, theme, None, None))
                continue
            share, mean = compare(mac_path, win_path, os.path.join(args.out, f"{key}-{theme}.jpg"))
            rows.append((key, theme, share, mean))
    with open(os.path.join(args.out, "report.md"), "w") as report:
        report.write("| page | theme | pixels differing | mean difference |\n|---|---|---|---|\n")
        for key, theme, share, mean in rows:
            if share is None:
                report.write(f"| {key} | {theme} | missing | |\n")
            else:
                report.write(f"| {key} | {theme} | {share * 100:.1f}% | {mean:.1f} |\n")
    for key, theme, share, mean in rows:
        print(f"{key:18} {theme:5} " + ("missing" if share is None else f"{share * 100:5.1f}%  mean {mean:5.1f}"))


if __name__ == "__main__":
    sys.exit(main())
