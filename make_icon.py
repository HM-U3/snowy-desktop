"""生成 DesktopRain 的应用图标 app.ico（水滴 + 斜向雨丝）。

用法： python make_icon.py
"""
from PIL import Image, ImageDraw
import math

S = 2048
CX, CY, R = 0.5, 0.655, 0.300
APEX = (0.5, 0.045)

FILL = (178, 205, 236, 255)      # 与原版雨丝同色
STROKE = (44, 84, 128, 255)
CLEAR = (0, 0, 0, 0)


def quad(p0, p1, p2, n=160):
    out = []
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        out.append((u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
                    u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1]))
    return out


def build():
    dist = CY - APEX[1]
    ang = math.asin(R / dist)
    tlx, tly = CX - R * math.cos(ang), CY - R * math.sin(ang)
    trx = CX + R * math.cos(ang)

    # 1) 轮廓掩膜：底部圆 + 两侧贝塞尔收口
    mask = Image.new('L', (S, S), 0)
    md = ImageDraw.Draw(mask)
    md.ellipse([(CX - R) * S, (CY - R) * S, (CX + R) * S, (CY + R) * S], fill=255)

    ctrl_y = APEX[1] + (tly - APEX[1]) * 0.42
    left = quad(APEX, (CX - (CX - tlx) * 0.62, ctrl_y), (tlx, tly))
    right = quad(APEX, (CX + (trx - CX) * 0.62, ctrl_y), (trx, tly))
    poly = [(p[0] * S, p[1] * S) for p in left + right[::-1]]
    md.polygon(poly, fill=255)

    # 2) 主体
    body = Image.new('RGBA', (S, S), CLEAR)
    body.paste(Image.new('RGBA', (S, S), FILL), (0, 0), mask)

    # 3) 雨丝：先画在独立图层，裁进水滴后再叠加（不能直接 composite，否则盖掉主体）
    lines = Image.new('RGBA', (S, S), CLEAR)
    ld = ImageDraw.Draw(lines)
    width = int(S * 0.052)
    for off in (-0.135, 0.0, 0.135):
        ld.line([((CX + off - 0.048) * S, (CY - 0.09) * S),
                 ((CX + off + 0.038) * S, (CY + 0.17) * S)],
                fill=STROKE, width=width)
    body = Image.alpha_composite(body, Image.composite(lines, Image.new('RGBA', (S, S), CLEAR), mask))

    # 4) 左上高光
    hl = Image.new('RGBA', (S, S), CLEAR)
    hd = ImageDraw.Draw(hl)
    hd.ellipse([(CX - R * 0.60) * S, (CY - R * 0.60) * S,
                (CX - R * 0.16) * S, (CY - R * 0.16) * S],
               fill=(255, 255, 255, 105))
    body = Image.alpha_composite(body, Image.composite(hl, Image.new('RGBA', (S, S), CLEAR), mask))

    return body


if __name__ == '__main__':
    icon = build()
    icon.save('icon_1024.png')
    icon.save('app.ico', sizes=[(s, s) for s in (256, 128, 64, 48, 40, 32, 24, 20, 16)])
    print('app.ico 已生成')
