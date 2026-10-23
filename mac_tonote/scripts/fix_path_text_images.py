#!/usr/bin/env python3
"""把正文里的「本地图片路径文本」换成真正的内联图片，并修正图片显示尺寸。

背景：
    用户从微信「复制路径」拿到的是一段路径文本，直接粘贴只会变成一串字符。
    新版应用已支持粘贴自动成图，但历史数据里已经存下的路径文本需要清理。

同时修正：
    图片的 width/height 若与附件文件的真实尺寸不一致（例如历史污染留下的
    160×90 占位值，而文件实际是 380×178），按真实尺寸重写。

安全约束：
    - 运行前自动备份 notes.json，并打印前后哈希
    - 只改 contentFormat == richText 的笔记
    - 源文件不存在 → 原样保留，不做任何猜测
    - 不删除任何外部文件

用法：
    python3 scripts/fix_path_text_images.py [--dry-run]
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
EXT = r"(?:png|jpe?g|gif|webp|heic|heif|tiff?|bmp)"


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


def rebuild_img(attrs: dict) -> str:
    parts = []
    if attrs.get("src"):
        parts.append(f'src="{attrs["src"]}"')
    for key in ("width", "height"):
        if key in attrs:
            parts.append(f'{key}="{attrs[key]}"')
    return "<img " + " ".join(parts) + ">"


def clean_src(src: str) -> str:
    """剥离 src 里混入的 width=/height= 尾巴（可能叠加多组）"""
    return re.sub(r"\s+(?:(?:width|height)\s*=\s*[\d.]+\s*)+$", "", src).strip()


def find_paths(text: str):
    """找出文本中指向真实存在的图片路径，返回 [(start, end, url)]"""
    results = []
    for m in re.finditer(r"/[^\s\"'<>]*?" + EXT + r"\b", text, re.IGNORECASE):
        raw = m.group(0)
        # src 被污染过：尾部混了 width=/height=
        raw = clean_src(raw)
        candidate = pathlib.Path(raw)
        if candidate.suffix.lower().lstrip(".") not in SUPPORTED:
            continue
        if not candidate.exists() or not candidate.is_file():
            continue
        results.append((m.start(), m.start() + len(raw), candidate))
    return results


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

        # 1) 路径文本 → 内联图片
        def replace_paths(match):
            inner = match.group(1)
            paths = find_paths(inner)
            if not paths:
                return match.group(0)
            out = []
            cursor = 0
            for start, end, source in paths:
                out.append(inner[cursor:start])
                target_dir = attachments_dir / note["id"]
                target_dir.mkdir(parents=True, exist_ok=True)
                ext = source.suffix.lower().lstrip(".")
                filename = f"{uuid.uuid4()}.{ext}"
                target = target_dir / filename
                shutil.copy2(source, target)
                relative = f"attachments/{note['id']}/{filename}"
                size = image_size(target)
                dim = f' width="{size[0]}" height="{size[1]}"' if size else ""
                out.append(f'<img src="{relative}"{dim}>')
                report.append((note["title"], "路径转内联图片", inner[start:end], relative))
                cursor = end
            out.append(inner[cursor:])
            return "<p>" + "".join(out) + "</p>"

        body = re.sub(r"<p>(.*?)</p>", replace_paths, body, flags=re.DOTALL)

        # 2) 修正 img 尺寸：以附件文件真实尺寸为准
        def fix_dims(match):
            attrs = parse_attrs(match.group(1))
            src = attrs.get("src")
            if not src or not src.startswith("attachments/"):
                return match.group(0)
            src = clean_src(src)
            attrs["src"] = src
            absolute = data_dir / src
            size = image_size(absolute) if absolute.exists() else None
            if size:
                if attrs.get("width") != str(size[0]) or attrs.get("height") != str(size[1]):
                    report.append((note["title"], "修正图片尺寸",
                                   f'{attrs.get("width")}x{attrs.get("height")}',
                                   f'{size[0]}x{size[1]}'))
                    attrs["width"] = str(size[0])
                    attrs["height"] = str(size[1])
            return rebuild_img(attrs)

        body = IMG_TAG.sub(fix_dims, body)

        if body != original:
            note["body"] = body

    if not report:
        print("✓ 未发现需要处理的内容")
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
