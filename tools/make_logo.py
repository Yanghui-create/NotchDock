#!/usr/bin/env python3
"""
NotchDock Logo 生成器（可复现，仅依赖 Pillow + numpy）

设计：
  · 主体 = 屏幕顶部一条圆角胶囊 + 中央向下凸出的「刘海」，即产品形态本身
  · 左侧三枚色点 = 收藏 / 剪贴板 / 待办 三个功能页
  · 刘海内一枚蓝紫渐变光点 = 摄像头 / 传感器，也像即将垂下的面板
  · 深空蓝黑底 + 冷调渐变描边 + 顶部高光，呼应「黑色 / 白色 / 液态玻璃」三套外观

输出：
  assets/logo.svg            矢量主标（浅色背景）
  assets/logo-mono.svg       矢量反白标（深色背景）
  assets/logo.png            512×512
  assets/logo-{256,128,64,32}.png
  assets/logo-mono.png       512×512 反白
  assets/NotchDock.ico       多尺寸应用图标
  NotchDock/Assets/NotchDock.ico   （同一份，供 WPF ApplicationIcon 引用）

用法：python tools/make_logo.py
"""

from __future__ import annotations

import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"
PROJ_ASSETS = ROOT / "NotchDock" / "Assets"

C = 512                     # 逻辑画布
S = 4                       # 超采样倍数
SS = C * S

# ---- 几何（与 SVG 中的 path 一一对应）------------------------------------
BAR = (40, 92, 472, 236)    # 顶部胶囊
BAR_R = 52
NOTCH = (188, 92, 324, 322)  # 中央下垂的刘海
NOTCH_R = 44
STROKE = 4                   # 描边宽度（逻辑像素）

# ---- 配色 ----------------------------------------------------------------
BODY_1 = (43, 48, 80)       # #2B3050 左上
BODY_2 = (18, 21, 38)       # #121526 中
BODY_3 = (10, 12, 22)       # #0A0C16 右下
EDGE_1 = (143, 180, 255)    # #8FB4FF
EDGE_2 = (110, 124, 255)    # #6E7CFF
EDGE_3 = (192, 123, 255)    # #C07BFF
DOTS = [(104, 164, "#4DA3FF", (77, 163, 255)),
        (142, 164, "#B57BFF", (181, 123, 255)),
        (180, 164, "#5BE7C4", (91, 231, 196))]
BALL_C = (256, 232)
BALL_R = 30


def _grid() -> tuple[np.ndarray, np.ndarray]:
    yy, xx = np.mgrid[0:C, 0:C]
    return xx.astype(float), yy.astype(float)


def _lerp(a, b, t: np.ndarray) -> np.ndarray:
    return a + (b - a) * t[..., None]


def _radial(cx: float, cy: float, radius: float, power: float = 1.5) -> np.ndarray:
    xx, yy = _grid()
    d = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / radius
    return np.clip(1.0 - d, 0.0, 1.0) ** power


def _gradient_rgb() -> Image.Image:
    """主体对角渐变 + 两团冷调辉光，输出 C×C RGB。"""
    xx, yy = _grid()
    t = (xx + yy) / (2.0 * (C - 1))
    a = np.clip(t / 0.55, 0, 1)
    b = np.clip((t - 0.55) / 0.45, 0, 1)
    rgb = _lerp(np.array(BODY_1, float), np.array(BODY_2, float), a)
    rgb = rgb + (np.array(BODY_3, float) - np.array(BODY_2, float)) * b[..., None]

    g1 = _radial(140, 110, 300) * 0.42
    g2 = _radial(410, 300, 260) * 0.30
    rgb = rgb + g1[..., None] * np.array([70, 95, 185], float)
    rgb = rgb + g2[..., None] * np.array([95, 45, 140], float)
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB")


def _edge_rgb() -> Image.Image:
    """描边用的蓝紫渐变，输出 C×C RGB。"""
    xx, yy = _grid()
    t = np.clip((xx * 0.65 + yy * 0.75) / (C * 1.4), 0, 1)
    a = np.clip(t / 0.5, 0, 1)
    b = np.clip((t - 0.5) / 0.5, 0, 1)
    rgb = _lerp(np.array(EDGE_1, float), np.array(EDGE_2, float), a)
    rgb = rgb + (np.array(EDGE_3, float) - np.array(EDGE_2, float)) * b[..., None]
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB")


def _mask_small() -> Image.Image:
    """形状的 alpha 蒙版（C×C）。形态学运算都在这一层做，避免大图滤波。"""
    m = Image.new("L", (C, C), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle(BAR, radius=BAR_R, fill=255)
    d.rounded_rectangle(NOTCH, radius=NOTCH_R, fill=255)
    return m


def _mask() -> Image.Image:
    return _mask_small().resize((SS, SS), Image.Resampling.LANCZOS)


def _solid(color: tuple[int, int, int], alpha: int = 255) -> Image.Image:
    return Image.new("RGBA", (SS, SS), (*color, alpha))


def _composite(base: Image.Image, layer: Image.Image) -> None:
    """按 layer 自身 alpha 叠加到 base。"""
    base.alpha_composite(layer)


def render_logo(mono: bool = False, glow: bool = True) -> Image.Image:
    """渲染 512×512 RGBA 主标。
    glow=False 用于 .ico：去掉外发光/光晕，保证 16px 托盘尺寸不发糊。
    """
    m = _mask()
    art = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))

    body = _gradient_rgb().resize((SS, SS), Image.Resampling.BICUBIC).convert("RGBA")
    edge = _edge_rgb().resize((SS, SS), Image.Resampling.BICUBIC).convert("RGBA")

    if mono:
        body = _solid((255, 255, 255))
        edge = _solid((255, 255, 255))

    ms = _mask_small()

    # 1. 外发光（只在彩色版、且展示用）
    if not mono and glow:
        halo = ms.filter(ImageFilter.MaxFilter(2 * 14 + 1))
        halo = halo.filter(ImageFilter.GaussianBlur(11))
        halo = halo.resize((SS, SS), Image.Resampling.BILINEAR)
        glow_layer = _solid((110, 130, 255))
        glow_layer.putalpha(halo.point(lambda v: int(v * 0.16)))
        _composite(art, glow_layer)

    # 2. 描边：膨胀后的蒙版减去原蒙版 = 一圈亮边
    dil = ms.filter(ImageFilter.MaxFilter(2 * STROKE + 1))
    ring = ImageChops.subtract(dil, ms).resize((SS, SS), Image.Resampling.BILINEAR)
    ring_layer = edge.copy()
    ring_layer.putalpha(ring)
    _composite(art, ring_layer)

    # 3. 主体渐变
    body.putalpha(m)
    _composite(art, body)

    dr = ImageDraw.Draw(art)

    # 4. 顶部内缘高光（横向 transparent → white → transparent）
    if not mono:
        hi = Image.new("RGBA", (C, C), (0, 0, 0, 0))
        hd = ImageDraw.Draw(hi)
        for x in range(90, 422):
            p = (x - 90) / 332
            a = int(150 * np.sin(np.pi * p) ** 1.4)
            hd.line([(x, 98), (x, 101)], fill=(255, 255, 255, a), width=1)
        hi = hi.resize((SS, SS), Image.Resampling.BILINEAR)
        hi.putalpha(ImageChops.multiply(hi.getchannel("A"), m))
        _composite(art, hi)

    # 5. 左侧三枚功能色点
    for x, y, _, rgb in DOTS:
        c = (255, 255, 255) if mono else rgb
        dr.ellipse(((x - 11) * S, (y - 11) * S, (x + 11) * S, (y + 11) * S), fill=(*c, 255))
        if not mono:
            dr.ellipse(((x - 7) * S, (y - 8) * S, (x - 1) * S, (y - 2) * S),
                       fill=(255, 255, 255, 110))

    # 6. 刘海内的渐变光点（摄像头 / 传感器）
    cx, cy = BALL_C
    if not mono:
        # 柔和光晕（.ico 版跳过，避免小尺寸糊成一团）
        if glow:
            halo2 = Image.new("RGBA", (C, C), (0, 0, 0, 0))
            gd = ImageDraw.Draw(halo2)
            gd.ellipse((cx - 62, cy - 62, cx + 62, cy + 62), fill=(124, 140, 255, 90))
            halo2 = halo2.filter(ImageFilter.GaussianBlur(16))
            halo2 = halo2.resize((SS, SS), Image.Resampling.BILINEAR)
            _composite(art, halo2)

        # 球体：径向渐变
        r = BALL_R * S
        yy, xx = np.mgrid[0:2 * r + 1, 0:2 * r + 1]
        d = np.sqrt((xx - r) ** 2 + (yy - r) ** 2) / r
        ball = np.zeros((2 * r + 1, 2 * r + 1, 4), np.uint8)
        top = np.array([176, 206, 255], float)
        mid = np.array([110, 124, 255], float)
        bot = np.array([84, 58, 200], float)
        t = np.clip(d, 0, 1)
        col = top + (mid - top) * np.clip(t / 0.55, 0, 1)[..., None]
        col = col + (bot - mid) * np.clip((t - 0.55) / 0.45, 0, 1)[..., None]
        ball[..., 0:3] = np.clip(col, 0, 255).astype(np.uint8)
        ball[..., 3] = (np.clip(1.0 - np.clip((d - 0.96) / 0.04, 0, 1), 0, 1) * 255).astype(np.uint8)
        ball_img = Image.fromarray(ball, "RGBA")
        art.alpha_composite(ball_img, ((cx * S - r), (cy * S - r)))
        dr.ellipse(((cx - 11) * S, (cy - 14) * S, (cx - 3) * S, (cy - 6) * S),
                   fill=(255, 255, 255, 165))
    else:
        dr.ellipse(((cx - BALL_R) * S, (cy - BALL_R) * S,
                    (cx + BALL_R) * S, (cy + BALL_R) * S), fill=(255, 255, 255, 255))

    # 7. 底部内缘折射线（冷蓝，压出玻璃厚度）
    if not mono:
        refr = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))
        rd = ImageDraw.Draw(refr)
        rd.arc(((NOTCH[0] + 6) * S, (NOTCH[3] - 52) * S,
                (NOTCH[2] - 6) * S, (NOTCH[3] + 8) * S),
               start=0, end=180, fill=(150, 180, 255, 90), width=2 * S)
        refr.putalpha(ImageChops.multiply(refr.getchannel("A"), m))
        _composite(art, refr)

    # 注意：这里不能再乘一次原蒙版，否则会把「描边环 / 外发光」裁掉。
    # 主体渐变、顶部高光、折射线各自已经用 m 裁过，其余层本来就该留在形状外。
    return art.resize((C, C), Image.Resampling.LANCZOS)


# ---- SVG（与上面几何/配色同源）--------------------------------------------
_OUTLINE = (
    "M 92,92 L 420,92 "
    "Q 472,92 472,144 L 472,184 Q 472,236 420,236 "
    "L 324,236 L 324,278 Q 324,322 280,322 "
    "L 232,322 Q 188,322 188,278 L 188,236 "
    "L 92,236 Q 40,236 40,184 L 40,144 Q 40,92 92,92 Z"
)


def svg_main() -> str:
    dots = "\n".join(
        f'  <circle cx="{x}" cy="{y}" r="11" fill="{h}"/>'
        f'\n  <circle cx="{x-4}" cy="{y-5}" r="3.2" fill="#FFFFFF" opacity="0.42"/>'
        for x, y, h, _ in DOTS
    )
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512" role="img" aria-label="NotchDock logo">
  <defs>
    <linearGradient id="nd-body" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2B3050"/>
      <stop offset="0.55" stop-color="#121526"/>
      <stop offset="1" stop-color="#0A0C16"/>
    </linearGradient>
    <linearGradient id="nd-edge" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#8FB4FF"/>
      <stop offset="0.5" stop-color="#6E7CFF"/>
      <stop offset="1" stop-color="#C07BFF"/>
    </linearGradient>
    <linearGradient id="nd-hilite" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0"/>
      <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0.6"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>
    <radialGradient id="nd-ball" cx="0.38" cy="0.3" r="0.75">
      <stop offset="0" stop-color="#B0CEFF"/>
      <stop offset="0.55" stop-color="#6E7CFF"/>
      <stop offset="1" stop-color="#543AC8"/>
    </radialGradient>
    <radialGradient id="nd-glow" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#8CA0FF" stop-opacity="0.35"/>
      <stop offset="1" stop-color="#8CA0FF" stop-opacity="0"/>
    </radialGradient>
    <filter id="nd-blur" x="-60%" y="-60%" width="220%" height="220%">
      <feGaussianBlur stdDeviation="14"/>
    </filter>
    <clipPath id="nd-clip"><path d="{_OUTLINE}"/></clipPath>
  </defs>

  <g clip-path="url(#nd-clip)">
    <path d="{_OUTLINE}" fill="url(#nd-body)"/>
    <circle cx="140" cy="110" r="170" fill="#4660C0" opacity="0.20" filter="url(#nd-blur)"/>
    <circle cx="410" cy="300" r="150" fill="#7A3AA0" opacity="0.16" filter="url(#nd-blur)"/>
    <path d="M 100,98 L 412,98" stroke="url(#nd-hilite)" stroke-width="3" stroke-linecap="round"/>
    <path d="M 196,310 Q 256,338 316,310" stroke="#96B4FF" stroke-width="2" fill="none" opacity="0.28"/>
  </g>
  <path d="{_OUTLINE}" fill="none" stroke="#8CA0FF" stroke-width="14" opacity="0.18" filter="url(#nd-blur)"/>
  <path d="{_OUTLINE}" fill="none" stroke="url(#nd-edge)" stroke-width="4"/>

  {dots}

  <circle cx="256" cy="232" r="58" fill="url(#nd-glow)"/>
  <circle cx="256" cy="232" r="30" fill="url(#nd-ball)"/>
  <circle cx="249" cy="222" r="6" fill="#FFFFFF" opacity="0.62"/>
</svg>
'''


def svg_mono() -> str:
    dots = "\n".join(f'  <circle cx="{x}" cy="{y}" r="11" fill="#FFFFFF"/>' for x, y, _, _ in DOTS)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512" role="img" aria-label="NotchDock logo (mono)">
  <path d="{_OUTLINE}" fill="#FFFFFF"/>
  {dots}
  <circle cx="256" cy="232" r="30" fill="#FFFFFF"/>
</svg>
'''


def save_ico(png: Image.Image, path: Path) -> None:
    """Pillow 生成标准多尺寸 ICO（小尺寸用 DIB，大尺寸用 PNG）。"""
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    png.save(path, format="ICO", sizes=sizes)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    PROJ_ASSETS.mkdir(parents=True, exist_ok=True)

    color = render_logo(mono=False, glow=True)      # 展示用：带氛围光
    icon = render_logo(mono=False, glow=False)       # 应用图标：干净锐利
    mono = render_logo(mono=True)

    (ASSETS / "logo.svg").write_text(svg_main(), encoding="utf-8")
    (ASSETS / "logo-mono.svg").write_text(svg_mono(), encoding="utf-8")
    color.save(ASSETS / "logo.png")
    mono.save(ASSETS / "logo-mono.png")

    for n in (256, 128, 64, 32):
        color.resize((n, n), Image.Resampling.LANCZOS).save(ASSETS / f"logo-{n}.png")

    save_ico(icon, ASSETS / "NotchDock.ico")
    save_ico(icon, PROJ_ASSETS / "NotchDock.ico")

    # 自检：确认 ICO 能被重新打开且尺寸齐全
    check = Image.open(ASSETS / "NotchDock.ico")
    print(f"ICO sizes: {sorted(check.info.get('sizes', []))}")
    for p in sorted(ASSETS.iterdir()):
        print(f"  {p.name:22s} {p.stat().st_size:>9,d} B")
    print(f"  NotchDock/Assets/NotchDock.ico  {(PROJ_ASSETS / 'NotchDock.ico').stat().st_size:>9,d} B")


if __name__ == "__main__":
    main()
