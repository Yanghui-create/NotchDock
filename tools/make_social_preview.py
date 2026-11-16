#!/usr/bin/env python3
"""
生成 GitHub 仓库 Social preview 图（1280×640，中文标题，< 1 MB，不透明背景）。

用法：python tools/make_social_preview.py
输出：C:\\Users\\yh421\\Desktop\\notchdock-social-preview.png
仅依赖 Pillow + numpy；中文字体用 Windows 自带的微软雅黑。
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H = 1280, 640
OUT = Path(r"C:\Users\yh421\Desktop\notchdock-social-preview.png")
LOGO = Path(r"D:\workbuudy\2026-11-15-07-06-51\assets\logo.png")

F_BOLD = "C:/Windows/Fonts/msyhbd.ttc"      # 微软雅黑 Bold
F_REG = "C:/Windows/Fonts/msyh.ttc"          # 微软雅黑
F_SEGOE_B = "C:/Windows/Fonts/seguisb.ttf"   # Segoe UI Semibold
F_SEGOE = "C:/Windows/Fonts/segoeui.ttf"

yy, xx = np.mgrid[0:H, 0:W]


def glow(cx: float, cy: float, rx: float, ry: float, color, strength: float) -> np.ndarray:
    d = np.sqrt(((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2)
    a = np.clip(1.0 - d, 0.0, 1.0) ** 1.7 * strength
    return a[..., None] * np.array(color, float)


def background() -> Image.Image:
    t = (xx / W) * 0.55 + (yy / H) * 0.45
    top = np.array([27, 34, 68], float)      # 深蓝紫
    bottom = np.array([9, 11, 20], float)    # 近黑
    rgb = top + (bottom - top) * t[..., None]
    rgb += glow(240, 110, 620, 460, (58, 92, 210), 0.40)
    rgb += glow(1090, 400, 540, 420, (120, 62, 190), 0.32)
    rgb += glow(720, 640, 460, 260, (30, 130, 170), 0.14)
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("RGBA")


def top_highlight(img: Image.Image) -> None:
    """顶部一条极细的镜面高光，呼应 Logo 的玻璃质感。"""
    layer = Image.new("RGBA", (W, 4), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for x in range(0, W, 2):
        a = int(120 * np.sin(np.pi * x / W) ** 1.6)
        d.line([(x, 1), (x, 2)], fill=(190, 210, 255, a), width=1)
    img.alpha_composite(layer, (0, 0))


def bottom_bar(img: Image.Image) -> None:
    """底部 8px 三色渐变：三枚功能色点（收藏 / 剪贴板 / 待办）。"""
    stops = [(77, 163, 255), (181, 123, 255), (91, 231, 196)]
    row = np.zeros((1, W, 3), np.uint8)
    for i in range(W):
        p = i / (W - 1) * (len(stops) - 1)
        k = min(int(p), len(stops) - 2)
        f = p - k
        row[0, i] = np.array(stops[k]) * (1 - f) + np.array(stops[k + 1]) * f
    bar = Image.fromarray(np.repeat(row, 8, axis=0), "RGB")
    # 两端淡出
    alpha = np.clip(np.minimum(np.arange(W) / 160, (W - 1 - np.arange(W)) / 160), 0, 1)
    bar.putalpha(Image.fromarray((alpha * 255).astype(np.uint8), "L").resize((W, 8)))
    img.alpha_composite(bar.convert("RGBA"), (0, H - 8))


def logo_halo(img: Image.Image) -> None:
    """Logo 背后一团柔和光晕，把深色主体从深色底上托起来。"""
    size = 560
    gy, gx = np.mgrid[0:size, 0:size]
    d = np.sqrt(((gx - size / 2) / (size / 2)) ** 2 + ((gy - size / 2) / (size / 2)) ** 2)
    a = (np.clip(1.0 - d, 0, 1) ** 1.6 * 105).astype(np.uint8)
    halo = Image.new("RGBA", (size, size), (96, 124, 255, 0))
    halo.putalpha(Image.fromarray(a, "L"))
    img.alpha_composite(halo, (1030 - size // 2, 310 - size // 2))


def main() -> None:
    img = background()
    top_highlight(img)
    logo_halo(img)

    logo = Image.open(LOGO).convert("RGBA").resize((400, 400), Image.Resampling.LANCZOS)
    img.alpha_composite(logo, (830, 112))

    d = ImageDraw.Draw(img)
    f_brand = ImageFont.truetype(F_SEGOE_B, 26)
    f_cn_small = ImageFont.truetype(F_REG, 22)
    f_title = ImageFont.truetype(F_BOLD, 64)
    f_sub = ImageFont.truetype(F_REG, 34)
    f_feat = ImageFont.truetype(F_REG, 24)
    f_url = ImageFont.truetype(F_SEGOE, 22)

    d.text((96, 110), "NotchDock", font=f_brand, fill=(150, 181, 255, 255))
    d.text((258, 114), "WINDOWS 桌面工具", font=f_cn_small, fill=(116, 132, 172, 255))

    d.text((94, 174), "桌面刘海，一碰就展开", font=f_title, fill=(246, 248, 255, 255))

    for i, c in enumerate([(77, 163, 255), (181, 123, 255), (91, 231, 196)]):
        x = 98 + i * 22
        d.ellipse((x, 274, x + 10, 284), fill=(*c, 255))

    d.text((96, 302), "收藏 · 剪贴板历史 · 待办提醒", font=f_sub, fill=(183, 197, 232, 255))
    d.text((96, 370), "悬停毫秒级展开  ·  Win+V 呼出剪贴板  ·  MIT 开源",
           font=f_feat, fill=(128, 142, 178, 255))
    d.text((96, 548), "github.com/Yanghui-create/NotchDock",
           font=f_url, fill=(98, 112, 148, 255))

    bottom_bar(img)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    img.convert("RGB").save(OUT, "PNG", optimize=True)
    print(f"saved: {OUT}  {OUT.stat().st_size:,d} B  ({W}x{H})")
    if OUT.stat().st_size > 1024 * 1024:
        print("WARNING: 超过 GitHub 1 MB 限制")


if __name__ == "__main__":
    main()
