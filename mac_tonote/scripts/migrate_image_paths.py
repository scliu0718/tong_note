#!/usr/bin/env python3
"""把旧版本遗留的「图片路径文本」还原成真正的内联图片。

背景：
    早期版本的图片按钮只插入 `![](图片地址)` 占位符，
    粘贴图片也只会得到一段绝对路径文本，而不是真正的图片。

本脚本做两件事：
    1. 把附件复制到 WorkLog 附件目录 attachments/<note-uuid>/<image-uuid>.<ext>
    2. 把正文里的路径文本替换为 <img src="attachments/...">

安全约束：
    - 只处理 contentFormat == richText 的笔记（旧 Markdown 笔记不动）
    - 源文件不存在的路径一律保留原样，不做任何猜测
    - 运行前自动备份 notes.json
    - 不删除任何外部文件

用法：
    python3 scripts/migrate_image_paths.py [--dry-run]
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

WORKSPACE = pathlib.Path(__file__).resolve().parent.parent
SUPPORTED = {"png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tiff", "tif", "bmp"}

# 匹配形如 /Users/xxx/yyy.png 的绝对路径文本（段落内整段就是路径）
PATH_TEXT = re.compile(r"^/\S+\.(?:png|jpe?g|gif|webp|heic|heif|tiff?|bmp)$", re.IGNORECASE)


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def image_size(path: pathlib.Path):
    """读取图片像素尺寸，失败返回 None"""
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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true", help="只报告将要做的变更，不写入")
    args = parser.parse_args()

    data_dir = pathlib.Path.home() / "Library/Application Support/WorkLog"
    notes_file = data_dir / "notes.json"
    attachments_dir = data_dir / "attachments"

    if not notes_file.exists():
        print(f"✗ 找不到 {notes_file}")
        return 1

    notes = json.loads(notes_file.read_text(encoding="utf-8"))
    print(f"▶ 读取 {len(notes)} 条笔记")

    changes = []
    for note in notes:
        if note.get("contentFormat") != "richText":
            continue

        body = note.get("body", "")
        if "<img" in body:
            continue  # 已有真实图片，跳过

        lines = body.split("\n")
        new_lines = []
        note_changed = False

        for line in lines:
            stripped = line.strip()
            # 只处理 <p>路径</p> 这种整段就是路径的情况
            match = re.match(r"^<p>(.*)</p>$", stripped)
            if not match:
                new_lines.append(line)
                continue

            inner = match.group(1).strip()
            if not PATH_TEXT.match(inner):
                new_lines.append(line)
                continue

            source = pathlib.Path(inner)
            ext = source.suffix.lower().lstrip(".")
            if not source.exists() or ext not in SUPPORTED:
                print(f"  · 跳过（源文件不存在或不支持）：{inner}")
                new_lines.append(line)
                continue

            # 复制到附件目录
            target_dir = attachments_dir / note["id"]
            target_dir.mkdir(parents=True, exist_ok=True)
            filename = f"{uuid.uuid4()}.{ext}"
            target = target_dir / filename
            shutil.copy2(source, target)

            relative = f"attachments/{note['id']}/{filename}"
            size = image_size(target)
            dim = f' width="{size[0]}" height="{size[1]}"' if size else ""

            new_lines.append(f'<p><img src="{relative}"{dim}></p>')
            note_changed = True
            changes.append((note["title"], inner, relative))

        if note_changed:
            note["body"] = "\n".join(new_lines)

    if not changes:
        print("✓ 没有需要迁移的图片路径")
        return 0

    print(f"\n▶ 发现 {len(changes)} 处待迁移：")
    for title, src, rel in changes:
        print(f"  · {title}")
        print(f"    来源：{src}")
        print(f"    目标：{rel}")

    if args.dry_run:
        print("\n(dry-run，未写入)")
        return 0

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = notes_file.with_name(f"notes.json.backup-{stamp}")
    shutil.copy2(notes_file, backup)
    print(f"\n▶ 已备份：{backup.name}")
    print(f"  源哈希  {sha256(notes_file)}")
    print(f"  备份哈希 {sha256(backup)}")

    # 原子写入
    tmp = notes_file.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(notes, ensure_ascii=False, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(tmp, notes_file)
    print(f"✓ 已写入 {notes_file}")
    print(f"  新哈希  {sha256(notes_file)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
