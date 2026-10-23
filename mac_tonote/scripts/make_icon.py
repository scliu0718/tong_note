#!/usr/bin/env python3
"""生成「同笔记」应用图标。

用法:
    make_icon.py OUT_DIR [--preview PNG_PATH]

    - OUT_DIR  : 输出目录，将在其中生成 AppIcon.icns
    - --preview: 额外输出一张 1024x1024 预览 PNG 到指定路径

依赖: Pillow（macOS 上还需 iconutil 以生成 .icns）
"""
import argparse
import os
import subprocess
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZE = 1024

# 品牌绿（与 App 内 Theme.accent 同系，自上而下由亮到深）
C_TOP = (44, 203, 128)     # #2CCB80
C_BOTTOM = (8, 132, 72)    # #088448
C_GLYPH = (255, 255, 255)  # 白色「同」字


def _lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def find_font(size):
    candidates = [
        "/System/Library/Fonts/Hiragino Sans GB.ttc",
        "/System/Library/Fonts/STHeiti Medium.ttc",
        "/System/Library/Fonts/STHeiti Light.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/Songti.ttc",
    ]
    for path in candidates:
        if not os.path.exists(path):
            continue
        for index in range(6):
            try:
                return ImageFont.truetype(path, size, index=index)
            except Exception:
                continue
    return None


def make_master():
    s = SIZE
    # 1) 背景：自上而下绿色渐变
    bg = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(bg)
    for y in range(s):
        c = _lerp(C_TOP, C_BOTTOM, y / (s - 1))
        d.line([(0, y), (s, y)], fill=c + (255,))

    # 2) 顶部柔和高光
    hl = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-int(s * 0.18), -int(s * 0.38), int(s * 0.82), int(s * 0.55)],
               fill=(255, 255, 255, 110))
    hl = hl.filter(ImageFilter.GaussianBlur(s * 0.11))
    bg = Image.alpha_composite(bg, hl)

    # 3) 「同」字（含柔和投影）
    font = find_font(int(s * 0.58))
    if font:
        shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        sd = ImageDraw.Draw(shadow)
        sd.text((s // 2, int(s * 0.53)), "同", font=font,
                fill=(4, 70, 40, 130), anchor="mm")
        shadow = shadow.filter(ImageFilter.GaussianBlur(s * 0.006))
        bg = Image.alpha_composite(bg, shadow)

        d = ImageDraw.Draw(bg)
        d.text((s // 2, int(s * 0.515)), "同", font=font,
               fill=C_GLYPH + (255,), anchor="mm")
    else:
        # 兜底：三条白色「笔记线」，避免无字体时图标空白
        d = ImageDraw.Draw(bg)
        m = s // 2
        w = s // 9
        gap = s // 6
        for dx in (-1, 0, 1):
            x = m + dx * gap
            d.line([x, s * 0.30, x, s * 0.70], fill=(255, 255, 255, 255), width=w)

    # 4) 圆角方形遮罩（macOS Big Sur 风格 squircle，约 22.5% 圆角）
    mask = Image.new("L", (s, s), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([0, 0, s - 1, s - 1], radius=round(s * 0.225), fill=255)
    bg.putalpha(mask)
    return bg


def build_iconset(master):
    """由 1024 主图生成 .iconset 目录（返回目录路径）。"""
    iconset = tempfile.mkdtemp(prefix="AppIcon.", suffix=".iconset")
    specs = {
        "icon_16x16.png": 16,
        "icon_16x16@2x.png": 32,
        "icon_32x32.png": 32,
        "icon_32x32@2x.png": 64,
        "icon_128x128.png": 128,
        "icon_128x128@2x.png": 256,
        "icon_256x256.png": 256,
        "icon_256x256@2x.png": 512,
        "icon_512x512.png": 512,
        "icon_512x512@2x.png": 1024,
    }
    for name, px in specs.items():
        master.resize((px, px), Image.LANCZOS).save(os.path.join(iconset, name))
    return iconset


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--preview", default=None)
    args = ap.parse_args()

    master = make_master()

    if args.preview:
        master.save(args.preview)
        print(f"preview -> {args.preview}")

    os.makedirs(args.out_dir, exist_ok=True)
    iconset = build_iconset(master)
    iconutil = subprocess.run(["xcrun", "-f", "iconutil"],
                              capture_output=True, text=True).stdout.strip()
    if iconutil:
        icns = os.path.join(args.out_dir, "AppIcon.icns")
        r = subprocess.run([iconutil, "-c", "icns", iconset, "-o", icns],
                           capture_output=True, text=True)
        if r.returncode != 0:
            print("iconutil 失败:", r.stderr, file=sys.stderr)
            sys.exit(1)
        print(f"icns -> {icns}")
    else:
        print("⚠ 未找到 iconutil，跳过 .icns 生成", file=sys.stderr)


if __name__ == "__main__":
    main()
