#!/usr/bin/env python3
"""Extract one member from a remote zip without downloading the archive.

    fetch-zip-member.py <zip-url> [member-path] [out-file]

With no member-path, lists every entry. A zip's central directory sits at its end, so this
reads the tail with a ranged request, locates the end-of-central-directory record, fetches
the directory exactly, and then fetches only the requested member's bytes - which is what
makes the shipped av1an.exe (a few MB) reachable out of a 485 MB win-x64 release zip. The
sandbox's egress proxy passes github.com/<repo>/releases/download/ URLs and honours range
requests; api.github.com it does not, for repositories not attached to the session.

Classic zip only (no zip64): fine for these releases, which stay under 4 GB and 65k entries.
"""
import struct
import sys
import urllib.request
import zlib


def fetch(url, start, end):
    # Absolute ranges only: the sandbox's proxy serves "bytes=a-b" and answers a suffix
    # range ("bytes=-N") with 501 Unsupported client range - measured, not guessed. That is
    # why the size is probed first instead of asking for "the last N bytes".
    req = urllib.request.Request(url, headers={"Range": f"bytes={start}-{end}"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def probe_size(url):
    req = urllib.request.Request(url, headers={"Range": "bytes=0-0"})
    with urllib.request.urlopen(req, timeout=120) as r:
        cr = r.headers.get("Content-Range", "")
        if "/" in cr:
            return int(cr.rsplit("/", 1)[1])
        sys.exit(f"no Content-Range in the range probe (got {r.status}) - server refuses ranges?")


def central_directory(url):
    size = probe_size(url)
    window = min(1 << 20, size)  # last 1 MiB holds EOCD + usually the whole CD
    tail = fetch(url, size - window, size - 1)
    eocd = tail.rfind(b"PK\x05\x06")
    if eocd < 0:
        sys.exit("no end-of-central-directory record in the last 1 MiB - not a classic zip?")
    _, _, _, count, cd_size, cd_offset, _ = struct.unpack_from("<HHHHIIH", tail, eocd + 4)
    # The tail may already contain the whole directory; otherwise fetch it exactly.
    if eocd >= cd_size:
        cd = tail[eocd - cd_size:eocd]
    else:
        cd = fetch(url, cd_offset, cd_offset + cd_size - 1)
    entries = {}
    i = 0
    while i + 46 <= len(cd) and cd[i:i + 4] == b"PK\x01\x02":
        method, = struct.unpack_from("<H", cd, i + 10)
        csize, usize = struct.unpack_from("<II", cd, i + 20)
        nlen, elen, clen = struct.unpack_from("<HHH", cd, i + 28)
        lho, = struct.unpack_from("<I", cd, i + 42)
        name = cd[i + 46:i + 46 + nlen].decode("utf-8", "replace")
        entries[name] = (method, csize, usize, lho)
        i += 46 + nlen + elen + clen
    if len(entries) != count:
        print(f"warning: parsed {len(entries)} of {count} directory entries", file=sys.stderr)
    return entries


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    url = sys.argv[1]
    entries = central_directory(url)
    if len(sys.argv) < 3:
        for name, (_, csize, usize, _) in entries.items():
            print(f"{usize:>12}  {name}")
        return
    member = sys.argv[2]
    out = sys.argv[3] if len(sys.argv) > 3 else member.rsplit("/", 1)[-1]
    if member not in entries:
        hits = [n for n in entries if member in n]
        sys.exit(f"no such member; close matches: {hits[:10]}")
    method, csize, usize, lho = entries[member]
    # The local header repeats the name/extra with its own (possibly different) extra length,
    # so the data offset has to be read from it rather than assumed from the directory's.
    lh = fetch(url, lho, lho + 29)
    if lh[:4] != b"PK\x03\x04":
        sys.exit("local header not where the directory said - zip64 or damaged archive")
    nlen, elen = struct.unpack_from("<HH", lh, 26)
    data_start = lho + 30 + nlen + elen
    data = fetch(url, start=data_start, end=data_start + csize - 1)
    if method == 8:
        data = zlib.decompressobj(-15).decompress(data)
    elif method != 0:
        sys.exit(f"unsupported compression method {method}")
    if len(data) != usize:
        sys.exit(f"size mismatch: got {len(data)}, directory says {usize}")
    with open(out, "wb") as f:
        f.write(data)
    print(f"wrote {out} ({usize} bytes) from {member}")


if __name__ == "__main__":
    main()
