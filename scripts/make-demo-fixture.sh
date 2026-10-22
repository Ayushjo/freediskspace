#!/bin/bash
# A lived-in demo Mac for marketing renders (the website film and screenshots).
# Sizes are realistic but the files are SPARSE: their logical size is large and
# they take almost no disk space. Render with `--size-basis logical`, or every
# size reads as zero. Demo data only — label renders "captured with demo files".
# Usage: scripts/make-demo-fixture.sh [dir]   (default "/tmp/Demo Mac")
set -euo pipefail
ROOT="${1:-/tmp/Demo Mac}"
case "$ROOT" in
  /tmp/*|/private/tmp/*) ;;
  *) echo "refusing to rebuild $ROOT: use a folder under /tmp" >&2; exit 2 ;;
esac
rm -rf "$ROOT"
python3 - "$ROOT" <<'PY'
import os, sys, time
root = sys.argv[1]
now = time.time(); day = 86400
GB = 1_000_000_000; MB = 1_000_000
# path, logical size, age in days
files = [
    ("Movies/Wedding edit 4K.mov", 26.1 * GB, 410),
    ("Movies/Holiday 2023.mov", 18.4 * GB, 380),
    ("Movies/Screen recordings/Demo take 3.mov", 2.2 * GB, 40),
    ("Movies/Screen recordings/Demo take 4.mov", 2.1 * GB, 40),
    ("Pictures/Photos Library.photoslibrary/originals/A/IMG_0001.heic", 21.4 * GB, 30),
    ("Pictures/Photos Library.photoslibrary/originals/B/IMG_0002.heic", 16.8 * GB, 30),
    ("Music/Logic/Album sessions.logicx", 4.3 * GB, 200),
    ("Downloads/Xcode_26.xip", 11.2 * GB, 150),
    ("Downloads/Figma-old.dmg", 0.42 * GB, 300),
    ("Downloads/ubuntu-24.04-desktop.iso", 6.1 * GB, 260),
    ("Downloads/archive-2022.zip", 3.8 * GB, 800),
    ("Downloads/invoice-march.pdf", 2.4 * MB, 20),
    ("Documents/Thesis/thesis-final-v7.pdf", 84 * MB, 200),
    ("Documents/Taxes/2025.pdf", 6 * MB, 120),
    ("Projects/dashboard/node_modules/next/dist/compiled.js", 1.4 * GB, 60),
    ("Projects/dashboard/node_modules/@swc/core/binary.node", 0.9 * GB, 60),
    ("Projects/dashboard/package-lock.json", 1.2 * MB, 12),
    ("Projects/webapp/node_modules/react/index.js", 0.8 * GB, 90),
    ("Projects/webapp/node_modules/typescript/lib/tsc.js", 0.6 * GB, 90),
    ("Projects/webapp/package-lock.json", 0.9 * MB, 10),
    ("Projects/rust-tool/target/debug/deps/rust_tool.rlib", 9.6 * GB, 45),
    ("Projects/rust-tool/Cargo.toml", 2_000, 45),
    ("Projects/ml-experiments/.venv/lib/python3.12/site-packages/torch/lib/libtorch.dylib", 4.1 * GB, 70),
    ("Projects/ml-experiments/requirements.txt", 1_000, 70),
    ("Library/Developer/Xcode/DerivedData/MyApp-abc123/Build/Intermediates.noindex/objects.o", 14.8 * GB, 20),
    ("Library/Developer/Xcode/iOS DeviceSupport/17.4 (21E219)/Symbols/dyld.bin", 4.1 * GB, 160),
    ("Library/Developer/Xcode/iOS DeviceSupport/16.7 (20H19)/Symbols/dyld.bin", 3.6 * GB, 420),
    ("Library/Developer/CoreSimulator/Caches/dyld/cache.bin", 2.9 * GB, 9),
    ("Library/Caches/com.spotify.client/Data/cache.bin", 1.9 * GB, 2),
    ("Library/Caches/Homebrew/downloads/bottles.tar.gz", 3.2 * GB, 14),
    ("Library/Caches/com.google.Chrome/Default/Cache/data.bin", 2.4 * GB, 1),
    ("Library/Caches/org.swift.swiftpm/repositories/pkgs.bin", 0.64 * GB, 25),
    ("Library/Application Support/Slack/Cache/data.bin", 1.1 * GB, 3),
    (".npm/_cacache/content-v2/sha512/blobs.bin", 5.6 * GB, 30),
    (".cache/huggingface/hub/models--llama/blobs/model.safetensors", 12.0 * GB, 100),
    (".cache/pip/http/cache.bin", 1.3 * GB, 50),
]
for rel, size, age in files:
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(rel.encode())          # a few real bytes so it isn't empty
        handle.truncate(int(size))          # the rest is a hole: no disk used
    stamp = now - age * day
    os.utime(path, (stamp, stamp))
# One real duplicate pair (small, real bytes) so Duplicates has something.
for rel in ("Downloads/Brand kit.zip", "Documents/Brand kit copy.zip"):
    path = os.path.join(root, rel)
    with open(path, "wb") as handle:
        handle.write(b"same bytes" * 300_000)
    os.utime(path, (now - 15 * day, now - 15 * day))
for current, dirs, _ in sorted(os.walk(root), key=lambda entry: -entry[0].count(os.sep)):
    os.utime(current, (now - 2 * day, now - 2 * day))
PY
echo "$ROOT"
