"""纯 ctypes 截屏工具（无需第三方库）：抓取指定窗口所在屏幕区域并存成 PNG。"""
import ctypes, struct, zlib, sys
from ctypes import wintypes

user32 = ctypes.WinDLL('user32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

# 关键：先声明 DPI 感知，否则 GetWindowRect 与 BitBlt 坐标系会错位
try:
    ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
except Exception:
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass

user32.FindWindowW.restype = wintypes.HWND
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.GetDC.restype = wintypes.HDC
user32.GetDC.argtypes = [wintypes.HWND]
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]

gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.BitBlt.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD]
gdi32.GetDIBits.argtypes = [wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
                            ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT]
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
gdi32.DeleteDC.argtypes = [wintypes.HDC]


class RECT(ctypes.Structure):
    _fields_ = [('left', wintypes.LONG), ('top', wintypes.LONG),
                ('right', wintypes.LONG), ('bottom', wintypes.LONG)]


class BIH(ctypes.Structure):
    _fields_ = [('biSize', wintypes.DWORD), ('biWidth', wintypes.LONG), ('biHeight', wintypes.LONG),
                ('biPlanes', wintypes.WORD), ('biBitCount', wintypes.WORD), ('biCompression', wintypes.DWORD),
                ('biSizeImage', wintypes.DWORD), ('biXPelsPerMeter', wintypes.LONG),
                ('biYPelsPerMeter', wintypes.LONG), ('biClrUsed', wintypes.DWORD),
                ('biClrImportant', wintypes.DWORD)]


def grab(x, y, w, h):
    hdc = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    old = gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, x, y, 0x00CC0020)

    bi = BIH()
    bi.biSize = ctypes.sizeof(BIH)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, hdc)
    return buf.raw


def save_png(path, w, h, bgra):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        base = y * w * 4
        for x in range(w):
            i = base + x * 4
            raw += bytes((bgra[i + 2], bgra[i + 1], bgra[i]))

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff))

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)
    blob = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 6)) + chunk(b'IEND', b''))
    open(path, 'wb').write(blob)


def main():
    title = sys.argv[1] if len(sys.argv) > 1 else 'DeskTidy'
    out = sys.argv[2] if len(sys.argv) > 2 else 'shot.png'
    pad = int(sys.argv[3]) if len(sys.argv) > 3 else 26

    hwnd = user32.FindWindowW(None, title)
    if not hwnd:
        print('WINDOW_NOT_FOUND')
        return 1

    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    x = max(0, r.left - pad)
    y = max(0, r.top - pad)
    w = (r.right - r.left) + pad * 2
    h = (r.bottom - r.top) + pad * 2

    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    w = min(w, sw - x)
    h = min(h, sh - y)

    print(f'RECT {r.left},{r.top} -> {r.right},{r.bottom}  capture {w}x{h}')
    save_png(out, w, h, grab(x, y, w, h))
    print('SAVED', out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
