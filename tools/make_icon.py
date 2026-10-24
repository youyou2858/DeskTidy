import struct, zlib, math, os

N = 256
cx0, cy0, cx1, cy1, R = 14.0, 14.0, 242.0, 242.0, 56.0

def sd_round_rect(px, py, x0, y0, x1, y1, r):
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    hx, hy = (x1 - x0) / 2.0 - r, (y1 - y0) / 2.0 - r
    dx, dy = abs(px - cx) - hx, abs(py - cy) - hy
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    return math.sqrt(ox * ox + oy * oy) + min(max(dx, dy), 0.0) - r

def lerp(a, b, t):
    return a + (b - a) * t

# ---- base layer: rounded square with vertical gradient ----
base = [[(0, 0, 0, 0)] * N for _ in range(N)]
for y in range(N):
    for x in range(N):
        px, py = x + 0.5, y + 0.5
        d = sd_round_rect(px, py, cx0, cy0, cx1, cy1, R)
        a = max(0.0, min(1.0, 0.5 - d))
        if a <= 0:
            continue
        t = (py - cy0) / (cy1 - cy0)
        # deep charcoal -> slightly lifted bottom
        r0 = lerp(24, 30, t); g0 = lerp(26, 32, t); b0 = lerp(33, 41, t)
        # top-left sheen
        sheen = max(0.0, 1.0 - (px * 0.55 + py) / 300.0) * 26.0
        base[y][x] = (int(r0 + sheen), int(g0 + sheen), int(b0 + sheen * 1.1), int(a * 255))

def blend(buf, x, y, col, alpha):
    if x < 0 or y < 0 or x >= N or y >= N or alpha <= 0:
        return
    br, bg, bb, ba = buf[y][x]
    aa = alpha
    buf[y][x] = (
        int(col[0] * aa + br * (1 - aa)),
        int(col[1] * aa + bg * (1 - aa)),
        int(col[2] * aa + bb * (1 - aa)),
        int(max(ba, int(aa * 255))),
    )

def draw_bar(buf, x0, y0, x1, y1, r, col, alpha):
    for y in range(int(y0) - 2, int(y1) + 3):
        for x in range(int(x0) - 2, int(x1) + 3):
            d = sd_round_rect(x + 0.5, y + 0.5, x0, y0, x1, y1, r)
            a = max(0.0, min(1.0, 0.5 - d)) * alpha
            if a > 0:
                blend(buf, x, y, col, a)

# ---- inner marks: one accent bar + two softer bars (the "zones") ----
ACCENT = (108, 140, 255)
WHITE = (245, 245, 250)
draw_bar(base, 62, 74, 194, 92, 9, ACCENT, 1.0)     # selected zone
draw_bar(base, 62, 112, 168, 128, 8, WHITE, 0.62)   # zone 2
draw_bar(base, 62, 148, 132, 164, 8, WHITE, 0.34)   # zone 3

# ---- subtle inner top highlight (glass rim) ----
for x in range(N):
    for y in range(int(cy0), int(cy0) + 26):
        px, py = x + 0.5, y + 0.5
        d = sd_round_rect(px, py, cx0, cy0, cx1, cy1, R)
        if -1.2 < d < 0.4:
            fade = max(0.0, 1.0 - (y - cy0) / 26.0)
            blend(base, x, y, (255, 255, 255), 0.16 * fade)

def to_png(px, size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            r, g, b, a = px[y][x]
            raw += bytes((r, g, b, a))
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))

def downscale(src, size):
    f = N / size
    out = [[(0, 0, 0, 0)] * size for _ in range(size)]
    for y in range(size):
        for x in range(size):
            rs = gs = bs = as_ = 0
            n = 0
            for sy in range(int(y * f), int((y + 1) * f)):
                for sx in range(int(x * f), int((x + 1) * f)):
                    r, g, b, a = src[sy][sx]
                    rs += r * a; gs += g * a; bs += b * a; as_ += a
                    n += 1
            if as_ == 0:
                out[y][x] = (0, 0, 0, 0)
            else:
                out[y][x] = (int(rs / as_), int(gs / as_), int(bs / as_), int(as_ / n))
    return out

sizes = [256, 128, 64, 48, 32, 16]
imgs = []
for s in sizes:
    imgs.append((s, to_png(base if s == N else downscale(base, s), s)))

out = bytearray()
out += struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
for (s, data) in imgs:
    dim = 0 if s >= 256 else s
    out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for (s, data) in imgs:
    out += data

path = r"D:\WorkBuddy AI\2026-10-22-23-28-24\DeskTidy\app.ico"
with open(path, "wb") as f:
    f.write(out)
print("written", path, len(out), "bytes")
