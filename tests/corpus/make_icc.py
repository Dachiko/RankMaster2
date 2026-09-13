"""A real ICC v2 matrix/TRC profile with Adobe RGB (1998) primaries.

Written out by hand because there is no free-standing Adobe RGB profile on this box and PIL's
ImageCms can only synthesise sRGB. A file named wide_gamut.jpg that carries an sRGB profile is
worse than no file at all: it passes the test while exercising nothing.
"""
import struct

def s15(x):                       # s15Fixed16Number
    return struct.pack(">i", round(x * 65536))

def xyz(x, y, z):
    return b"XYZ " + b"\0" * 4 + s15(x) + s15(y) + s15(z)

def curv(gamma):                  # single-value curve = pure gamma, u8Fixed8
    return b"curv" + b"\0" * 4 + struct.pack(">I", 1) + struct.pack(">H", round(gamma * 256))

def desc(text):                   # textDescriptionType, ICC v2
    a = text.encode("ascii") + b"\0"
    return (b"desc" + b"\0" * 4 + struct.pack(">I", len(a)) + a
            + struct.pack(">I", 0) + struct.pack(">I", 0)
            + struct.pack(">H", 0) + bytes([0]) + b"\0" * 67)

def text(t):
    return b"text" + b"\0" * 4 + t.encode("ascii") + b"\0"

# Adobe RGB (1998) primaries, Bradford-adapted to the D50 PCS the ICC spec requires.
tags = [
    (b"desc", desc("Adobe RGB (1998) - synthesised for RankMaster2 tests")),
    (b"wtpt", xyz(0.96420, 1.00000, 0.82491)),          # D50
    (b"rXYZ", xyz(0.60974, 0.31111, 0.01947)),
    (b"gXYZ", xyz(0.20528, 0.62567, 0.06087)),
    (b"bXYZ", xyz(0.14919, 0.06322, 0.74457)),
    (b"rTRC", curv(2.19921875)),
    (b"gTRC", curv(2.19921875)),
    (b"bTRC", curv(2.19921875)),
    (b"cprt", text("Public domain test profile")),
]

table, blob, offset = b"", b"", 128 + 4 + 12 * len(tags)
for sig, data in tags:
    pad = (-len(data)) % 4
    table += sig + struct.pack(">II", offset, len(data))
    blob += data + b"\0" * pad
    offset += len(data) + pad

body = struct.pack(">I", len(tags)) + table + blob
size = 128 + len(body)
header = (
    struct.pack(">I", size) + b"\0" * 4 + struct.pack(">I", 0x02100000)
    + b"mntr" + b"RGB " + b"XYZ " + b"\0" * 12 + b"acsp" + b"\0" * 4
    + b"\0" * 4 + b"\0" * 4 + b"\0" * 4 + b"\0" * 8 + struct.pack(">I", 0)
    + s15(0.9642) + s15(1.0) + s15(0.8249) + b"\0" * 4 + b"\0" * 44
)
assert len(header) == 128, len(header)
ADOBE_RGB = header + body

if __name__ == "__main__":
    open("adobergb.icc", "wb").write(ADOBE_RGB)
    print(f"wrote adobergb.icc, {len(ADOBE_RGB)} bytes")
