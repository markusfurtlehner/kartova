"""Generate DirStat application icons with no third-party dependencies.

Produces a treemap glyph: nested rounded blocks on a deep slate field, in the
app's accent ramp. Writes PNGs at several sizes plus a multi-size .ico that
embeds those PNGs directly (supported by Windows Vista and later).
"""

import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "src" / "DirStat.App" / "Assets"

# Accent ramp, matching the UI theme.
BG_TOP = (0x12, 0x16, 0x22)
BG_BOT = (0x1B, 0x22, 0x33)
BLOCKS = [
    # x, y, w, h in a 0..1 unit square, colour
    (0.100, 0.100, 0.470, 0.470, (0x4C, 0x8D, 0xFF)),
    (0.590, 0.100, 0.310, 0.290, (0x37, 0xD6, 0xB0)),
    (0.590, 0.410, 0.310, 0.160, (0x8B, 0x7C, 0xFF)),
    (0.100, 0.590, 0.230, 0.310, (0xFF, 0xB2, 0x4C)),
    (0.350, 0.590, 0.220, 0.310, (0xFF, 0x6B, 0x8A)),
    (0.590, 0.590, 0.310, 0.310, (0x5A, 0xC8, 0xFA)),
]


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def rounded_coverage(px, py, x, y, w, h, r, ss=4):
    """Supersampled coverage of a rounded rect for one pixel."""
    hits = 0
    for sy in range(ss):
        for sx in range(ss):
            fx = px + (sx + 0.5) / ss
            fy = py + (sy + 0.5) / ss
            if not (x <= fx <= x + w and y <= fy <= y + h):
                continue
            # Distance into each corner arc.
            cx = min(max(fx, x + r), x + w - r)
            cy = min(max(fy, y + r), y + h - r)
            dx, dy = fx - cx, fy - cy
            if dx * dx + dy * dy <= r * r:
                hits += 1
    return hits / (ss * ss)


def render(size):
    """Return an RGBA bytearray of the icon at the given square size."""
    buf = bytearray(size * size * 4)

    # Background: vertical gradient with a soft rounded mask.
    outer_r = size * 0.22
    for py in range(size):
        t = py / max(size - 1, 1)
        br, bg, bb = lerp(BG_TOP, BG_BOT, t)
        for px in range(size):
            cov = rounded_coverage(px, py, 0, 0, size, size, outer_r)
            if cov <= 0:
                continue
            i = (py * size + px) * 4
            buf[i] = br
            buf[i + 1] = bg
            buf[i + 2] = bb
            buf[i + 3] = round(255 * cov)

    # Treemap blocks, each with a cushion-style vertical lift.
    block_r = max(size * 0.035, 1.0)
    for bx, by, bw, bh, colour in BLOCKS:
        x, y = bx * size, by * size
        w, h = bw * size, bh * size
        x0, y0 = int(x), int(y)
        x1, y1 = min(size, int(x + w) + 2), min(size, int(y + h) + 2)
        for py in range(y0, y1):
            # Lit from upper-left: brighten the top, deepen the bottom.
            t = (py - y) / max(h, 1)
            shade = 1.18 - 0.34 * t
            lit = tuple(min(255, max(0, round(c * shade))) for c in colour)
            for px in range(x0, x1):
                cov = rounded_coverage(px, py, x, y, w, h, block_r)
                if cov <= 0:
                    continue
                i = (py * size + px) * 4
                a = buf[i + 3] / 255 if buf[i + 3] else 0
                for k in range(3):
                    buf[i + k] = round(buf[i + k] * (1 - cov) + lit[k] * cov)
                buf[i + 3] = round(255 * min(1.0, a + cov))
    return buf


def png_bytes(rgba, size):
    """Encode an RGBA buffer as a PNG."""
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)  # filter type 0
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def ico_bytes(pngs):
    """Wrap PNGs into a multi-image .ico."""
    header = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries, blobs = bytearray(), bytearray()
    for size, data in pngs:
        entries += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0, 0, 1, 32, len(data), offset,
        )
        blobs += data
        offset += len(data)
    return header + bytes(entries) + bytes(blobs)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    pngs = []
    for s in ico_sizes:
        data = png_bytes(render(s), s)
        pngs.append((s, data))
        if s in (32, 128, 256):
            (OUT / f"dirstat-{s}.png").write_bytes(data)
        print(f"  rendered {s}x{s}")

    (OUT / "dirstat.ico").write_bytes(ico_bytes(pngs))
    (OUT / "dirstat.png").write_bytes(dict(pngs)[256])
    print(f"wrote icons to {OUT}")


if __name__ == "__main__":
    main()
