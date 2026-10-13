"""Turns an X window dump (the file Xvfb keeps of its screen, optionally gzipped) into a PNG.

    python xwd2png.py screen.xwd.gz screen.png

Only what Xvfb writes is handled: 24/32-bit true colour, one pixel per 4 bytes. Standard library only.
"""
import gzip
import struct
import sys
import zlib


def main():
    source, target = sys.argv[1], sys.argv[2]
    with open(source, "rb") as handle:
        data = handle.read()
    if data[:2] == b"\x1f\x8b":
        data = gzip.decompress(data)

    fields = struct.unpack(">25I", data[:100])
    header_size, width, height = fields[0], fields[4], fields[5]
    byte_order, bits_per_pixel, bytes_per_line, colours = fields[7], fields[11], fields[12], fields[19]
    if bits_per_pixel != 32:
        raise SystemExit("only 32 bits per pixel is supported, this dump has %d" % bits_per_pixel)

    pixels = data[header_size + colours * 12:]
    rows = []
    for y in range(height):
        line = pixels[y * bytes_per_line: y * bytes_per_line + width * 4]
        # least-significant byte first: blue, green, red, unused
        if byte_order == 0:
            rgb = bytearray(width * 3)
            rgb[0::3] = line[2::4]
            rgb[1::3] = line[1::4]
            rgb[2::3] = line[0::4]
        else:
            rgb = bytearray(width * 3)
            rgb[0::3] = line[1::4]
            rgb[1::3] = line[2::4]
            rgb[2::3] = line[3::4]
        rows.append(b"\x00" + bytes(rgb))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    with open(target, "wb") as handle:
        handle.write(png)
    print("%s: %d x %d" % (target, width, height))


if __name__ == "__main__":
    main()
