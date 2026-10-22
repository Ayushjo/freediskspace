"""A lived-in demo PC for parity renders: the Windows twin of scripts/make-demo-fixture.sh.

Same sizes and ages as the Mac demo, with Windows paths (Videos, AppData\\Local\\npm-cache,
.gradle\\caches, Android SDK images ...). Files are SPARSE: their logical size is large and
they take almost no disk space, so render with DISKMAP_SIZE_BASIS=logical. Point the app
at it with DISKMAP_HOME=<dir> so %LOCALAPPDATA% and friends resolve inside the demo.
Demo data only; never a real profile.

Usage: python windows\\scripts\\make-demo-fixture.py [dir]   (default C:\\DemoPC)
"""
import ctypes
import msvcrt
import os
import shutil
import sys
import time

ROOT = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else r"C:\DemoPC")
_base = os.path.basename(ROOT.rstrip("\\/")).lower()
if not (_base.startswith("demopc") or ROOT.lower().startswith(os.environ.get("TEMP", "~").lower())):
    sys.exit(f"refusing to rebuild {ROOT}: use a folder named DemoPC* or one under %TEMP%")

GB = 1_000_000_000
MB = 1_000_000
DAY = 86400
FSCTL_SET_SPARSE = 0x000900C4

# path, logical size, age in days
FILES = [
    (r"Videos\Wedding edit 4K.mov", 26.1 * GB, 410),
    (r"Videos\Holiday 2023.mov", 18.4 * GB, 380),
    (r"Videos\Captures\Demo take 3.mp4", 2.2 * GB, 40),
    (r"Videos\Captures\Demo take 4.mp4", 2.1 * GB, 40),
    (r"Pictures\Camera Roll\2025\IMG_0001.heic", 21.4 * GB, 30),
    (r"Pictures\Camera Roll\2025\IMG_0002.heic", 16.8 * GB, 30),
    (r"Music\FL Studio\Album sessions.flp", 4.3 * GB, 200),
    (r"Downloads\Unity-2026.1-offline.zip", 11.2 * GB, 150),
    (r"Downloads\FigmaSetup-old.exe", 0.42 * GB, 300),
    (r"Downloads\ubuntu-24.04-desktop.iso", 6.1 * GB, 260),
    (r"Downloads\archive-2022.zip", 3.8 * GB, 800),
    (r"Downloads\invoice-march.pdf", 2.4 * MB, 20),
    (r"Documents\Thesis\thesis-final-v7.pdf", 84 * MB, 200),
    (r"Documents\Taxes\2025.pdf", 6 * MB, 120),
    (r"Projects\dashboard\node_modules\next\dist\compiled.js", 1.4 * GB, 60),
    (r"Projects\dashboard\node_modules\@swc\core\binary.node", 0.9 * GB, 60),
    (r"Projects\dashboard\package-lock.json", 1.2 * MB, 12),
    (r"Projects\webapp\node_modules\react\index.js", 0.8 * GB, 90),
    (r"Projects\webapp\node_modules\typescript\lib\tsc.js", 0.6 * GB, 90),
    (r"Projects\webapp\package-lock.json", 0.9 * MB, 10),
    (r"Projects\rust-tool\target\debug\deps\rust_tool.rlib", 9.6 * GB, 45),
    (r"Projects\rust-tool\Cargo.toml", 2_000, 45),
    (r"Projects\ml-experiments\.venv\Lib\site-packages\torch\lib\torch_cpu.dll", 4.1 * GB, 70),
    (r"Projects\ml-experiments\requirements.txt", 1_000, 70),
    # The Mac demo's Xcode / simulator / Homebrew caches, as their Windows counterparts.
    (r".gradle\caches\transforms-4\objects.bin", 14.8 * GB, 20),
    (r"AppData\Local\Android\Sdk\system-images\android-35\google_apis\x86_64\system.img", 4.1 * GB, 160),
    (r"AppData\Local\Android\Sdk\system-images\android-33\google_apis\x86_64\system.img", 3.6 * GB, 420),
    (r"AppData\Local\NuGet\v3-cache\packages.bin", 2.9 * GB, 9),
    (r"AppData\Local\Spotify\Data\cache.bin", 1.9 * GB, 2),
    (r"AppData\Local\Yarn\Cache\v6\packages.bin", 3.2 * GB, 14),
    (r"AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\data_0", 2.4 * GB, 1),
    (r"AppData\Local\pnpm\cache\metadata.bin", 0.64 * GB, 25),
    (r"AppData\Roaming\Slack\Cache\Cache_Data\data.bin", 1.1 * GB, 3),
    (r"AppData\Local\npm-cache\_cacache\content-v2\sha512\blobs.bin", 5.6 * GB, 30),
    (r".cache\huggingface\hub\models--llama\blobs\model.safetensors", 12.0 * GB, 100),
    (r"AppData\Local\pip\cache\http\cache.bin", 1.3 * GB, 50),
]


def make_sparse(path: str, size: int) -> None:
    with open(path, "wb") as handle:
        handle.write(path.encode("utf-8"))  # a few real bytes so it isn't empty
        handle.flush()
        returned = ctypes.c_ulong(0)
        ok = ctypes.windll.kernel32.DeviceIoControl(
            ctypes.c_void_p(msvcrt.get_osfhandle(handle.fileno())), FSCTL_SET_SPARSE,
            None, 0, None, 0, ctypes.byref(returned), None)
        if not ok:
            raise OSError(ctypes.GetLastError(), f"couldn't mark {path} sparse; refusing to allocate {size} bytes")
        handle.truncate(int(size))  # the rest is a hole: no disk used


def main() -> None:
    if os.path.exists(ROOT):
        shutil.rmtree(ROOT)
    now = time.time()
    for rel, size, age in FILES:
        path = os.path.join(ROOT, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        make_sparse(path, size)
        stamp = now - age * DAY
        os.utime(path, (stamp, stamp))
    # One real duplicate pair (small, real bytes) so Duplicates has something.
    for rel in (r"Downloads\Brand kit.zip", r"Documents\Brand kit copy.zip"):
        path = os.path.join(ROOT, rel)
        with open(path, "wb") as handle:
            handle.write(b"same bytes" * 300_000)
        os.utime(path, (now - 15 * DAY, now - 15 * DAY))
    for current, _dirs, _files in sorted(os.walk(ROOT), key=lambda entry: -entry[0].count(os.sep)):
        os.utime(current, (now - 2 * DAY, now - 2 * DAY))
    print(ROOT)


if __name__ == "__main__":
    main()
