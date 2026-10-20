#!/usr/bin/env python3
"""
Generates dist/app.ico for SciBridge: a rounded blue tile with a white
"bring the data down to this machine" arrow.

Pure standard library (zlib + struct), and it prints an ASCII preview so the
shape can be checked without an image viewer.

Run:  python3 tools/make_icon.py
"""
import pathlib
import struct
import zlib

BACKGROUND = (0x1F, 0x6F, 0xEB)   # brand blue
FOREGROUND = (0xFF, 0xFF, 0xFF)
SS = 4                            # supersampling factor


def rounded_tile(size, radius, colour):
    """RGBA pixels of a rounded square, antialiased by supersampling."""
    big = size * SS
    r = radius * SS

    def inside(x, y):
        # Distance from the pixel to the nearest point of the inner rectangle.
        cx = min(max(x, r), big - 1 - r)
        cy = min(max(y, r), big - 1 - r)
        dx, dy = x - cx, y - cy
        return dx * dx + dy * dy <= r * r

    large = bytearray(big * big * 4)
    for y in range(big):
        base = y * big * 4
        for x in range(big):
            if inside(x, y):
                i = base + x * 4
                large[i] = colour[0]
                large[i + 1] = colour[1]
                large[i + 2] = colour[2]
                large[i + 3] = 255

    # Downsample: average each SS x SS block, which also smooths the edges.
    out = bytearray(size * size * 4)
    for y in range(size):
        for x in range(size):
            r_sum = g_sum = b_sum = a_sum = 0
            for dy in range(SS):
                row = (y * SS + dy) * big * 4
                for dx in range(SS):
                    i = row + (x * SS + dx) * 4
                    r_sum += large[i]
                    g_sum += large[i + 1]
                    b_sum += large[i + 2]
                    a_sum += large[i + 3]
            n = SS * SS
            o = (y * size + x) * 4
            a = a_sum // n
            if a == 0:
                continue
            out[o] = r_sum // n
            out[o + 1] = g_sum // n
            out[o + 2] = b_sum // n
            out[o + 3] = a
    return out


def draw_arrow(pixels, size):
    """White downward arrow with a base line, drawn as simple rectangles."""
    def put(x, y):
        if 0 <= x < size and 0 <= y < size:
            i = (y * size + x) * 4
            pixels[i:i + 3] = bytes(FOREGROUND)
            pixels[i + 3] = 255

    scale = size / 256.0
    shaft_half = int(15 * scale)
    shaft_top = int(52 * scale)
    shaft_bottom = int(132 * scale)
    head_half = int(52 * scale)
    head_top = int(124 * scale)
    head_bottom = int(190 * scale)
    base_half = int(70 * scale)
    base_top = int(204 * scale)
    base_bottom = int(222 * scale)

    cx = size // 2
    for y in range(shaft_top, shaft_bottom):
        for x in range(cx - shaft_half, cx + shaft_half):
            put(x, y)
    for y in range(head_top, head_bottom):
        # triangle narrowing downwards
        t = (y - head_top) / max(1, head_bottom - head_top)
        half = int(head_half * (1.0 - t))
        for x in range(cx - half, cx + half):
            put(x, y)
    for y in range(base_top, base_bottom):
        for x in range(cx - base_half, cx + base_half):
            put(x, y)


def png_rgba(width, height, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[y * width * 4:(y + 1) * width * 4]) for y in range(height))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) +
            chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def build_image(size):
    pixels = rounded_tile(size, max(2, int(48 * size / 256)), BACKGROUND)
    draw_arrow(pixels, size)
    return pixels


def ascii_preview(size=256, cols=44):
    pixels = build_image(size)
    lines = []
    for row in range(cols // 2):
        line = ""
        for col in range(cols):
            x = int(col * size / cols)
            y = int(row * size / (cols // 2))
            i = (y * size + x) * 4
            a = pixels[i + 3]
            if a < 110:
                line += "."          # transparent
            elif pixels[i] > 160 and pixels[i + 1] > 160:
                line += "#"          # white glyph
            else:
                line += "o"          # blue tile
        lines.append(line)
    return "\n".join(lines)


def main():
    out = pathlib.Path(__file__).resolve().parent.parent / "src" / "SciBridge.App" / "app.ico"
    out.parent.mkdir(parents=True, exist_ok=True)

    print("icon preview ('.' = transparent, 'o' = blue tile, '#' = white glyph)")
    print(ascii_preview())

    sizes = [256, 64, 48, 32, 16]
    images = [(s, png_rgba(s, s, build_image(s))) for s in sizes]

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data

    out.write_bytes(header + entries + blobs)
    print(f"\nwrote {out} ({out.stat().st_size} bytes, sizes={sizes})")


if __name__ == "__main__":
    main()
