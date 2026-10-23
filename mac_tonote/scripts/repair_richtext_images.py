#!/usr/bin/env python3
"""修复旧版本写入的畸形 <img> 标签与遗留的图片路径文本。

历史问题：
    1. 早期 HTML 属性解析器有缺陷，`<img src="a.png" width="1" height="2">`
       会被读成 src="a.png width=1 height=2"，导致图片加载失败；
    2. 旧版「粘贴图片」只得到一段绝对路径文本，而不是真正的内联图片。

本脚本：
    - 把 src 里混入的 width/height 属性剥离干净；
    - 把仍然存在的图片路径文本还原成真正的内联图片（源文件不存在则保留原样）。

安全约束：
    - 运行前自动备份 notes.json，并打印前后哈希
    - 只改 contentFormat == richText 的笔记
    - 不删除任何外部文件

用法：
    python3 scripts/repair_richtext_images.py [--dry-run]
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import sys
import time
import uuid

SUPPORTED = {"png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp"}
IMG_TAG = re.compile(r"<img\s+([^>]*?)/?>", re.IGNORECASE)
ATTR = re.compile(r"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(\"[^\"]*\"|'[^']*'|[^\s\"'>]+)")
PATH_TEXT = re.compile(r"^/(\S+)\.(png|jpe?g|gif|webp|heic|heif|tiff?|bmp)$", re.IGNORECASE)


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def image_size(path: pathlib.Path):
    try:
        out = os.popen(f"sips -g pixelWidth -g pixelHeight {json.dumps(str(path))} 2>/dev/null").read()
        w = h = None
        for line in out.splitlines():
            if "pixelWidth" in line:
                w = int(line.split(":")[-1].strip())
            elif "pixelHeight" in line:
                h = int(line.split(":")[-1].strip())
        if w and h:
            return w, h
    except Exception:
        pass
    return None


def parse_attrs(raw: str) -> dict:
    out = {}
    for m in ATTR.finditer(raw):
        key = m.group(1).lower()
        value = m.group(2).strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        if key not in out:
            out[key] = value
    return out


def clean_src(src: str) -> str:
    """把 src 里混入的 width=/height= 尾巴剥掉（可能叠加多组）"""
    return re.sub(r"\s+(?:(?:width|height)\s*=\s*[\d.]+\s*)+$", "", src).strip()


def rebuild_img(attrs: dict) -> str:
    parts = []
    src = attrs.get("src", "")
    if src:
        parts.append(f'src="{src}"')
    for key in ("width", "height"):
        if key in attrs:
            parts.append(f'{key}="{attrs[key]}"')
    return "<img " + " ".join(parts) + ">"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    data_dir = pathlib.Path.home() / "Library/Application Support/WorkLog"
    notes_file = data_dir / "notes.json"
    attachments_dir = data_dir / "attachments"

    if not notes_file.exists():
        print(f"✗ 找不到 {notes_file}")
        return 1

    notes = json.loads(notes_file.read_text(encoding="utf-8"))
    report = []

    for note in notes:
        if note.get("contentFormat") != "richText":
            continue
        body = note.get("body", "")
        original = body

        # 1) 修复畸形 img 标签
        def fix_tag(match):
            attrs = parse_attrs(match.group(1))
            if "src" not in attrs:
                return match.group(0)
            fixed = clean_src(attrs["src"])
            if fixed != attrs["src"]:
                attrs["src"] = fixed
                report.append((note["title"], "修复 img src", attrs["src"], match.group(0)))
            return rebuild_img(attrs)

        body = IMG_TAG.sub(fix_tag, body)

        # 2) 还原图片路径文本
        out_lines = []
        for line in body.split("\n"):
            stripped = line.strip()
            m = re.match(r"^<p>(.*)</p>$", stripped)
            if not m:
                out_lines.append(line)
                continue
            inner = m.group(1).strip()
            pm = PATH_TEXT.match(inner)
            if not pm:
                out_lines.append(line)
                continue
            source = pathlib.Path("/" + pm.group(1) + "." + pm.group(2))
            if not source.exists():
                out_lines.append(line)
                continue
            target_dir = attachments_dir / note["id"]
            target_dir.mkdir(parents=True, exist_ok=True)
            ext = source.suffix.lower().lstrip(".")
            filename = f"{uuid.uuid4()}.{ext}"
            target = target_dir / filename
            shutil.copy2(source, target)
            relative = f"attachments/{note['id']}/{filename}"
            size = image_size(target)
            dim = f' width="{size[0]}" height="{size[1]}"' if size else ""
            out_lines.append(f'<p><img src="{relative}"{dim}></p>')
            report.append((note["title"], "路径转内联图片", inner, relative))

        body = "\n".join(out_lines)
        if body != original:
            note["body"] = body

    if not report:
        print("✓ 未发现需要修复的内容")
        return 0

    print(f"▶ 发现 {len(report)} 处问题：")
    for title, kind, before, after in report:
        print(f"  · [{kind}] {title}")
        print(f"    前：{before[:110]}")
        print(f"    后：{after[:110]}")

    if args.dry_run:
        print("\n(dry-run，未写入)")
        return 0

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = notes_file.with_name(f"notes.json.backup-{stamp}")
    shutil.copy2(notes_file, backup)
    print(f"\n▶ 已备份：{backup.name}")
    print(f"  源哈希   {sha256(notes_file)}")
    print(f"  备份哈希 {sha256(backup)}")

    tmp = notes_file.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(notes, ensure_ascii=False, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(tmp, notes_file)
    print(f"✓ 已写入 {notes_file}")
    print(f"  新哈希   {sha256(notes_file)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
