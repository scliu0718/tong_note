#!/usr/bin/env python3
"""生成「同笔记」应用图标。

用法:
    make_icon.py OUT_DIR [--preview PNG_PATH]
    make_icon.py --android RES_DIR

    - OUT_DIR  : 输出目录，将在其中生成 AppIcon.icns
    - --preview: 额外输出一张 1024x1024 预览 PNG 到指定路径
    - --android: 把同一套绿底白「同」和笔尖写成 Android adaptive icon 的各密度 PNG
    - --windows: 写成 Windows 用的 .ico，并在旁边放一张同内容的 .png

依赖: Pillow（macOS 上还需 iconutil 以生成 .icns）
"""
import argparse
import math
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


def _gradient(size):
    """自上而下的品牌绿渐变，叠一层顶部高光。不含文字、不切圆角。"""
    bg = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(bg)
    for y in range(size):
        c = _lerp(C_TOP, C_BOTTOM, y / (size - 1))
        d.line([(0, y), (size, y)], fill=c + (255,))

    hl = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-int(size * 0.18), -int(size * 0.38), int(size * 0.82), int(size * 0.55)],
               fill=(255, 255, 255, 110))
    hl = hl.filter(ImageFilter.GaussianBlur(size * 0.11))
    return Image.alpha_composite(bg, hl)


# 选定的一版：字号占边长一半，「同」略偏上，最后一笔从内框右下收成笔尖。
_MARK_FONT_RATIO = 0.50
_MARK_CENTER_Y = 0.45


def _text_mask(size, font, cx, cy):
    im = Image.new("L", (size, size), 0)
    ImageDraw.Draw(im).text((cx, cy), "同", font=font, fill=255, anchor="mm")
    return im


def _inner_bottom(mask):
    """内框最后一横的左右端和所在行。找不到时返回 None。"""
    bbox = mask.getbbox()
    if bbox is None:
        return None
    x0, y0, x1, y1 = bbox
    best = None
    for y in range(y0, y1):
        xs = [x for x in range(x0, x1) if mask.getpixel((x, y)) > 128]
        if not xs:
            continue
        runs = []
        start = prev = xs[0]
        for x in xs[1:]:
            if x == prev + 1:
                prev = x
            else:
                runs.append((start, prev))
                start = prev = x
        runs.append((start, prev))
        for a, b in runs:
            width = b - a
            if 120 < width < (x1 - x0) * 0.62 and a > x0 + 50:
                key = (y, width)
                if best is None or key > best[0]:
                    best = (key, a, b, y)
    if best is None:
        return None
    _, a, b, y = best
    return a, b, y


def _add_nib(canvas, origin, tip, base):
    """从最后一横的右端画出钢笔尖，尖上留一道缝。"""
    ox, oy = origin
    tx, ty = tip
    dx, dy = tx - ox, ty - oy
    length = math.hypot(dx, dy) or 1
    ux, uy = dx / length, dy / length
    px, py = -uy, ux
    back = 4
    b1 = (ox - ux * back + px * base / 2, oy - uy * back + py * base / 2)
    b2 = (ox - ux * back - px * base / 2, oy - uy * back - py * base / 2)
    shoulder = 0.38
    s1 = (ox + ux * length * shoulder + px * base * 0.46,
          oy + uy * length * shoulder + py * base * 0.46)
    s2 = (ox + ux * length * shoulder - px * base * 0.46,
          oy + uy * length * shoulder - py * base * 0.46)
    ImageDraw.Draw(canvas).polygon(
        [b1, s1, (tx, ty), s2, b2], fill=(255, 255, 255, 255))
    slit_from = (ox + ux * length * 0.28, oy + uy * length * 0.28)
    alpha = canvas.getchannel("A")
    ImageDraw.Draw(alpha).line(
        [slit_from, (tx, ty)], fill=0, width=max(4, int(base * 0.12)))
    canvas.putalpha(alpha)


def _draw_mark(size):
    """透明底上的白色「同」和笔尖，带柔和投影。没有中文字体时返回空白。"""
    font = find_font(int(size * _MARK_FONT_RATIO))
    if font is None:
        return Image.new("RGBA", (size, size), (0, 0, 0, 0))
    cx, cy = size // 2, int(size * _MARK_CENTER_Y)
    white = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(white).text((cx, cy), "同", font=font, fill=C_GLYPH + (255,), anchor="mm")
    mask = _text_mask(size, font, cx, cy)
    stroke = _inner_bottom(mask)
    if stroke is not None:
        a, b, y = stroke
        _add_nib(
            white,
            (b - 18, y + 4),
            (a + (b - a) * 0.42, mask.getbbox()[3] + int(size * _MARK_FONT_RATIO * 0.16)),
            base=int(size * _MARK_FONT_RATIO * 0.085),
        )
    alpha = white.getchannel("A")
    solid = Image.new("RGBA", (size, size), (4, 70, 40, 255))
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow.paste(solid, (0, int(size * 0.012)), alpha.point(lambda v: 120 if v > 16 else 0))
    shadow = shadow.filter(ImageFilter.GaussianBlur(size * 0.007))
    return Image.alpha_composite(shadow, white)


def make_master():
    s = SIZE
    mark = _draw_mark(s)
    bg = _gradient(s)
    if mark.getbbox() is not None:
        bg = Image.alpha_composite(bg, mark)
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

    # 5) 缩小到 macOS 标准占位（1024 画布中图标占 824），四周留透明边距
    inner = round(s * 0.824)
    art = bg.resize((inner, inner), Image.LANCZOS)
    final = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    final.paste(art, ((s - inner) // 2, (s - inner) // 2), art)
    return final


_ANDROID_DENSITIES = {
    "mipmap-mdpi": 108,
    "mipmap-hdpi": 162,
    "mipmap-xhdpi": 216,
    "mipmap-xxhdpi": 324,
    "mipmap-xxxhdpi": 432,
}


def make_android_layers():
    """背景铺满渐变。前景按系统可见区域（中心 72/108）摆「同」和笔尖，避免圆形遮罩切到笔尖。"""
    mark = _draw_mark(round(SIZE * 72 / 108))
    foreground = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    offset = (SIZE - mark.size[0]) // 2
    foreground.paste(mark, (offset, offset), mark)
    return _gradient(SIZE), foreground


def export_windows(path):
    """Windows 用的 ico：圆角图标铺满，不含 macOS 那圈透明边。"""
    art = _gradient(SIZE)
    mark = _draw_mark(SIZE)
    if mark.getbbox() is None:
        print("没有可用的中文字体，Windows 图标未写入", file=sys.stderr)
        sys.exit(1)
    art = Image.alpha_composite(art, mark)
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, SIZE - 1, SIZE - 1], radius=round(SIZE * 0.225), fill=255)
    art.putalpha(mask)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    art.save(path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    png = os.path.splitext(path)[0] + ".png"
    art.save(png)
    print(f"windows -> {path}")
    print(f"windows -> {png}")


def export_android(res_dir):
    background, foreground = make_android_layers()
    if foreground.getbbox() is None:
        print("没有可用的中文字体，Android 图标未写入", file=sys.stderr)
        sys.exit(1)
    for folder, px in _ANDROID_DENSITIES.items():
        out = os.path.join(res_dir, folder)
        os.makedirs(out, exist_ok=True)
        background.resize((px, px), Image.LANCZOS).save(
            os.path.join(out, "ic_launcher_background.png"))
        foreground.resize((px, px), Image.LANCZOS).save(
            os.path.join(out, "ic_launcher_foreground.png"))
        print(f"android -> {out}")


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
    ap.add_argument("out_dir", nargs="?")
    ap.add_argument("--preview", default=None)
    ap.add_argument("--android", default=None, metavar="RES_DIR")
    ap.add_argument("--windows", default=None, metavar="ICO_PATH")
    args = ap.parse_args()

    if args.android:
        export_android(args.android)
    if args.windows:
        export_windows(args.windows)
    if not args.out_dir and not args.preview:
        if not args.android and not args.windows:
            ap.error("请指定 OUT_DIR、--android 或 --windows")
        return

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
