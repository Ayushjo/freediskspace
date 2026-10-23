"""Add Bootstrap icons from react-icons to windows/app/DiskMap.App/ReactIconData.cs.

  python3 windows/scripts/add-icons.py <path to node_modules/react-icons> BsBox BsHammer ...

Reads each icon's SVG paths from react-icons/bs/index.js (the same package the existing
entries came from) and inserts them before the "BsGrid" entry, skipping names already there.
Build-time only: the app embeds the path data and never loads React or Node.
"""
import json
import os
import re
import sys

ICONS = os.path.join(os.path.dirname(__file__), "..", "app", "DiskMap.App", "ReactIconData.cs")


def icon_paths(source, name):
    match = re.search(r"function " + re.escape(name) + r" \(props\) \{\s*return GenIcon\((\{.*?\})\)\(props\);", source, re.S)
    if not match:
        sys.exit(f"{name}: not in react-icons/bs")
    tree = json.loads(match.group(1))
    if tree["attr"].get("viewBox") != "0 0 16 16":
        sys.exit(f"{name}: unexpected viewBox {tree['attr'].get('viewBox')}")
    paths = []

    def walk(node):
        if node.get("tag") == "path":
            paths.append(node["attr"]["d"])
        for child in node.get("child", []):
            walk(child)

    walk(tree)
    return paths


def main():
    package, names = sys.argv[1], sys.argv[2:]
    source = open(os.path.join(package, "bs", "index.js"), encoding="utf-8").read()
    raw = open(ICONS, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    data = raw.decode("utf-8-sig")
    anchor = '        ["BsGrid"] = new(16, 16,'
    if anchor not in data:
        sys.exit("anchor entry BsGrid not found")
    added = []
    for name in names:
        if f'["{name}"]' in data:
            continue
        paths = ",\n".join(f'            "{p}"' for p in icon_paths(source, name))
        entry = f'        ["{name}"] = new(16, 16,\n{paths}\n        ),\n'
        data = data.replace(anchor, entry + anchor, 1)
        added.append(name)
    open(ICONS, "w", encoding="utf-8-sig" if bom else "utf-8", newline="").write(data)
    print("added:", " ".join(added) or "nothing")


if __name__ == "__main__":
    main()
