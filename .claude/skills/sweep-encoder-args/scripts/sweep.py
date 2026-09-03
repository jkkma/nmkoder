#!/usr/bin/env python3
"""
sweep.py - pass every value the encoder argument lists state to the real binaries, and report what
refuses.

The lists in Nmkoder/BinFiles/encoderArgs are what the Advanced grids offer, and the record's rule
for them is that a row states what the parser accepts (CLAUDE.md, "The Advanced tab"). The check
that enforces it - every example, every range end and every stated default passed to the binary -
was done twice by hand with two different ad-hoc extractors, and the second could not reproduce the
first's count (459 candidate values against 583). This script is the extractor and the runner in one
place, so the count is a property of the code rather than of whoever ran it last.

    python sweep.py                       # the five CLI binaries in ~/.nmkoder-dev/bin/av1an/enc
    python sweep.py --dry-run             # the extraction only: every value per row, and the counts
    python sweep.py --enc X265,SvtAv1     # a subset (list names, comma-separated)
    python sweep.py --only ref,tune       # some rows (by name, across the selected lists)
    python sweep.py --ffmpeg              # the encoderArgs/ffmpeg lists through the shipped ffmpeg
    python sweep.py --ffmpeg --gpu        # ...including the two NVENC lists (the GPU rule: ask first)
    python sweep.py --out DIR --jobs 4 --keep --source in.y4m

What it takes from a row: every example value; the two ends of a numeric range written at the head
of the short description (0-5, -7 to 7, 0.0-8.0) or an "up to N"; every token of a head that is purely
an enumeration (psnr, ssim, iq, ssimulacra2 / flat or jvt); and the "(default X)" token where X is a
number, an enumerated token or one of the examples. Nothing after the "(default" parenthetical is
ever read as a value, which is what keeps x265's rdoq-level from contributing the 4-6 that belongs to
rd. A head that is prose ("Float above 1.0", "the -intra variants and more") contributes nothing
beyond the examples. Rows whose head starts "Path to" are skipped and listed.

Two things the record calls harness artifacts are handled rather than reported as faults. Min/max
pairs (qm-min/qm-max, chroma-qm-min/-max, min-qp/max-qp, min-q/max-q, qpmin/qpmax) run with the
partner moved to the same value whenever the value alone would cross the partner's stated default,
and every such run is listed as paired. The stated default is compared byte-for-byte against a
blank run only where the encoder is deterministic - two blank runs identical, which SVT-AV1 gives
only with --lp 1, so that is in its base arguments - and reported as information, not a fault,
because defaults move with the preset.

Success is judged by artifacts: exit 0, an output past stub size (SVT-AV1 writes a 32-byte stub and
x265 nothing on a refusal), and no error line. The base arguments are the presets Quick Convert
opens on (SVT-AV1 4, aomenc cpu-used 6, vpxenc cpu-used 3, x264 and x265 medium), CRF rate control,
and --disable-warning-prompt for the two that ask questions on stdin - which is closed here as well,
so a build without the flag fails fast rather than hanging. A base argument is dropped when the row
under test is that parameter. The source is a 320x240 24 fps 8-bit 4:2:0 3 s y4m unless --source
names another; a refusal that names the profile, the bit depth or the chroma format may be the
fixture rather than the row, and the report says which binary and source every verdict came from.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve()
REPO = HERE.parents[4]
LISTS = {"av1an": REPO / "Nmkoder/BinFiles/encoderArgs/av1an", "ffmpeg": REPO / "Nmkoder/BinFiles/encoderArgs/ffmpeg"}


def winpath(p):
    """A Git Bash path (/c/Users/...) handed to a Windows Python becomes C:/Users/..."""
    if os.name == "nt" and p:
        m = re.match(r"^/([A-Za-z])/(.*)$", p)
        if m:
            return f"{m.group(1).upper()}:/{m.group(2)}"
    return p


HOME = Path(winpath(os.environ.get("USERPROFILE") or os.environ.get("HOME") or "~")).expanduser()
ENC_DIR = Path(winpath(os.environ.get("ENC_DIR") or str(HOME / ".nmkoder-dev/bin/av1an/enc")))
FFMPEG = winpath(os.environ.get("FFMPEG") or str(HOME / ".nmkoder-dev/bin/ffmpeg.exe"))
EXE = ".exe" if os.name == "nt" else ""

NUM = r"-?\d+(?:\.\d+)?"
STOP = {"to", "each", "above", "up", "for", "and", "the", "like", "a", "in", "no", "blank", "higher",
        "more", "variants", "units", "of", "or", "auto"}
ERR = re.compile(r"(error|invalid|unrecognized|unknown option|failed|not supported|unable|cannot)", re.I)
DROP = re.compile(r"(Error parsing option|Unknown option|has not been used for any stream|Invalid parameter)", re.I)
STUB = 256
BAD = ("refused", "dropped-silently", "harness-error")

# The CLI binaries, launched the way Quick Convert launches them (VideoEncodersDirect.cs), at the
# presets it opens on. eq=True is aomenc/vpxenc's --key=value spelling.
CLI = {
    "SvtAv1": dict(exe="SvtAv1EncApp", io=lambda s, o: ["-i", s, "-b", o],
                   base=[("preset", "4"), ("crf", "30"), ("lp", "1")], eq=False, ext="ivf", ver=["--version"]),
    "AomAv1": dict(exe="aomenc", io=lambda s, o: ["--ivf", "-o", o, s],
                   base=[("cpu-used", "6"), ("end-usage", "q"), ("cq-level", "30"), ("row-mt", "1"),
                         ("disable-warning-prompt", None)], eq=True, ext="ivf", ver=["--help"]),
    "Vpx": dict(exe="vpxenc", io=lambda s, o: ["--ivf", "-o", o, s],
                base=[("cpu-used", "3"), ("end-usage", "q"), ("cq-level", "30"), ("row-mt", "1"),
                      ("disable-warning-prompt", None)], eq=True, ext="ivf", ver=["--help"]),
    "X264": dict(exe="x264", io=lambda s, o: ["--demuxer", "y4m", "-o", o, s],
                 base=[("preset", "medium"), ("crf", "23")], eq=False, ext="264", ver=["--version"]),
    "X265": dict(exe="x265", io=lambda s, o: ["--y4m", "--input", s, "--output", o],
                 base=[("preset", "medium"), ("crf", "28")], eq=False, ext="265", ver=["--version"]),
}
# The ffmpeg lists, through the spelling FfmpegEncoderArgs gives each encoder: one params option for
# four of them, one AVOption per row for libvpx and NVENC. Raw elementary streams and IVF as the
# containers, never Matroska/WebM - those write a fresh SegmentUID per mux, so two identical encodes
# differ and the default-vs-blank comparison reads every row as broken.
FF = {
    "Libx264": dict(codec="libx264", style="params", opt="-x264-params", base=["-preset", "medium", "-crf", "23"], fmt="h264"),
    "Libx265": dict(codec="libx265", style="params", opt="-x265-params", base=["-preset", "medium", "-crf", "28"], fmt="hevc"),
    "LibSvtAv1": dict(codec="libsvtav1", style="params", opt="-svtav1-params", base=["-preset", "4", "-crf", "30"], fmt="ivf"),
    "LibAomAv1": dict(codec="libaom-av1", style="params", opt="-aom-params", base=["-cpu-used", "6", "-crf", "30", "-b:v", "0"], fmt="ivf"),
    "LibVpx": dict(codec="libvpx-vp9", style="avopt", base=["-cpu-used", "3", "-crf", "30", "-b:v", "0"], fmt="ivf"),
    "H264Nvenc": dict(codec="h264_nvenc", style="avopt", base=["-preset", "p4"], fmt="h264", gpu=True),
    "H265Nvenc": dict(codec="hevc_nvenc", style="avopt", base=["-preset", "p4"], fmt="hevc", gpu=True),
}


# ---- extraction -------------------------------------------------------------------------------------
def extract(row):
    name, short = row[0], row[2]
    examples = [e.split("|", 1)[0].strip() for e in (row[5] or "").split("\n") if e.strip()]
    head = short.split(" - ")[0].strip()
    info = dict(name=name, head=head, examples=examples, range=None, upper=None, enum=[], default=None, skipped=None)
    if head.lower().startswith("path to"):
        info["skipped"] = "path-valued row; its examples are illustrative paths"
        return info
    left = re.split(r"\s\(", head, 1)[0].strip()
    m = re.match(rf"^({NUM})-({NUM})(?![\d.])", left) or re.match(rf"^({NUM}) to ({NUM})(?![\d.])", left)
    if m:
        info["range"] = (m.group(1), m.group(2))
    else:
        u = re.search(r"\bup to (\d+)\b", left)
        if u:
            info["upper"] = u.group(1)
        toks = [t.strip() for t in re.split(r",\s*|\s+or\s+", left) if t.strip()]
        if len(toks) >= 2 and all(re.fullmatch(r"[a-z0-9][a-z0-9.-]*", t) for t in toks) and not (set(toks) & STOP):
            info["enum"] = toks
    d = re.search(r"\(default ([^\s,);]+)", head)
    if d:
        tok = d.group(1)
        if re.fullmatch(NUM, tok) or tok in info["enum"] or tok in examples:
            info["default"] = tok
    return info


def values_of(info):
    vals = list(info["examples"])
    if info["range"]:
        vals += list(info["range"])
    if info["upper"]:
        vals.append(info["upper"])
    vals += info["enum"]
    if info["default"]:
        vals.append(info["default"])
    seen, out = set(), []
    for v in vals:
        if v not in seen:
            seen.add(v)
            out.append(v)
    return out


def partner_of(name, names):
    for a, b in (("min", "max"), ("max", "min")):
        if a in name:
            cand = name.replace(a, b, 1)
            if cand != name and cand in names:
                return cand, a
    return None, None


def paired_args(info, val, infos):
    cand, kind = partner_of(info["name"], infos)
    if not cand:
        return None
    pd = infos[cand]["default"]
    try:
        v, d = float(val), float(pd)
    except (TypeError, ValueError):
        return None
    if (kind == "min" and v > d) or (kind == "max" and v < d):
        return (cand, val, pd)
    return None


# ---- running ----------------------------------------------------------------------------------------
def fmt_opt(k, v, eq):
    if v is None:
        return [f"--{k}"]
    return [f"--{k}={v}"] if eq else [f"--{k}", str(v)]


def cli_cmd(spec, src, out, params):
    keys = {k for k, _ in params}
    args = [str(ENC_DIR / (spec["exe"] + EXE))] + spec["io"](src, out)
    for k, v in spec["base"]:
        if k not in keys:
            args += fmt_opt(k, v, spec["eq"])
    for k, v in params:
        args += fmt_opt(k, v, spec["eq"])
    return args


def ff_cmd(spec, src, out, params):
    args = [FFMPEG, "-hide_banner", "-nostats", "-y", "-i", src, "-c:v", spec["codec"]] + list(spec["base"])
    if spec["style"] == "params":
        if params:
            args += [spec["opt"], ":".join(f"{k}={v}" for k, v in params)]
    else:
        for k, v in params:
            args += [f"-{k}", str(v)]
    return args + ["-f", spec["fmt"], out]


def run(args, out, timeout):
    t = time.time()
    try:
        p = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
        rc, log = p.returncode, p.stdout.decode("utf-8", "replace")
    except subprocess.TimeoutExpired:
        rc, log = None, f"TIMEOUT after {timeout}s"
    except OSError as e:
        rc, log = None, f"could not launch: {e}"
    out = Path(out)
    size = out.stat().st_size if out.exists() else 0
    md5 = hashlib.md5(out.read_bytes()).hexdigest() if size else ""
    return dict(rc=rc, ms=int((time.time() - t) * 1000), bytes=size, md5=md5, log=log, out_name=out.name)


def first_match(log, pattern, skip=""):
    # A line naming the output file is not a message about it: the file is named after the row,
    # and a row called error-resilient would otherwise match the error vocabulary on every run.
    for line in log.splitlines():
        if skip and skip in line:
            continue
        if pattern.search(line):
            return line.strip()[:200]
    return ""


def verdict(r, ffmpeg=False):
    err = first_match(r["log"], ERR, r.get("out_name", ""))
    if r["rc"] == 0 and r["bytes"] > STUB:
        if ffmpeg:
            drop = first_match(r["log"], DROP, r.get("out_name", ""))
            if drop:
                return "dropped-silently", drop
        return ("accepted-with-message" if err else "accepted"), err
    tail = [l for l in r["log"].splitlines() if l.strip()]
    return "refused", (err or (tail[-1].strip()[:200] if tail else "no output"))


def version_of(args):
    try:
        p = subprocess.run(args, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30)
    except Exception as e:  # noqa: BLE001 - a version line is a nicety
        return f"(could not run: {e})"
    lines = (p.stdout + p.stderr).splitlines()
    # The line that says "version" or names an encoder and carries a number, before any line that
    # merely carries one: x265's first numeric line is its GCC build, aomenc's banner is a usage.
    for line in lines:
        if re.search(r"(version|encoder)", line, re.I) and re.search(r"\d", line):
            return line.strip()[:120]
    for line in lines:
        if re.search(r"\d+\.\d+\.\d+", line):
            return line.strip()[:120]
    return "(no version line)"


def make_source(outdir):
    src = outdir / "source-320x240-24fps-3s.y4m"
    if not src.exists():
        subprocess.run([FFMPEG, "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24",
                        "-t", "3", "-pix_fmt", "yuv420p", str(src)], check=True)
    return src


# ---- the sweep --------------------------------------------------------------------------------------
def sweep_list(kind, enc, spec, src, outdir, opts, report):
    listfile = LISTS[kind] / f"{enc}.json"
    rows = json.load(open(listfile, encoding="utf-8-sig"))
    infos = {r[0]: extract(r) for r in rows}
    entry = dict(list=f"{kind}/{enc}.json", rows=len(rows), version="", deterministic=None, control_bytes=0,
                 skipped=[], runs=[], values=0)
    if kind == "av1an":
        exe = ENC_DIR / (spec["exe"] + EXE)
        if not exe.exists():
            entry["error"] = f"binary missing: {exe}"
            report.append(entry)
            return
        entry["version"] = version_of([str(exe)] + spec["ver"])
        cmd = lambda out, params: cli_cmd(spec, str(src), str(out), params)  # noqa: E731
        ext = spec["ext"]
    else:
        if spec.get("gpu") and not opts.gpu:
            entry["error"] = "needs the GPU; run with --gpu after asking (the ask-before-gpu-stress rule)"
            report.append(entry)
            return
        entry["version"] = version_of([FFMPEG, "-version"])
        cmd = lambda out, params: ff_cmd(spec, str(src), str(out), params)  # noqa: E731
        ext = spec["fmt"]
    odir = outdir / f"{kind}-{enc}"
    odir.mkdir(parents=True, exist_ok=True)

    tasks = []
    for name, info in infos.items():
        if opts.only and name not in opts.only:
            continue
        if info["skipped"]:
            entry["skipped"].append((name, info["skipped"]))
            continue
        for v in values_of(info):
            pair = paired_args(info, v, infos)
            params = [(name, v)] + ([(pair[0], pair[1])] if pair else [])
            tasks.append((name, v, pair, params, info["default"] == v))
    entry["values"] = len(tasks)
    entry["extraction"] = {n: dict(values=values_of(i), default=i["default"], range=i["range"], enum=i["enum"],
                                   upper=i["upper"]) for n, i in infos.items() if not i["skipped"]}
    if opts.dry_run:
        report.append(entry)
        return

    c1 = run(cmd(odir / f"blank1.{ext}", []), odir / f"blank1.{ext}", opts.timeout)
    c2 = run(cmd(odir / f"blank2.{ext}", []), odir / f"blank2.{ext}", opts.timeout)
    det = bool(c1["md5"]) and c1["md5"] == c2["md5"]
    entry["deterministic"], entry["control_bytes"] = det, c1["bytes"]
    if c1["bytes"] <= STUB:
        entry["error"] = f"the blank control did not encode (rc {c1['rc']}, {c1['bytes']} bytes): {first_match(c1['log'], ERR) or c1['log'][-200:]}"
        report.append(entry)
        return

    def do(it):
        i, (name, v, pair, params, is_default) = it
        # The index is part of the name: three master-display examples share their first 40
        # characters, and two parallel runs writing one file had one delete it under the other.
        out = odir / f"{name}__{i:03d}_{re.sub(r'[^A-Za-z0-9.-]', '_', v)[:30]}.{ext}"
        try:
            r = run(cmd(out, params), out, opts.timeout)
            vd, msg = verdict(r, ffmpeg=(kind == "ffmpeg"))
            dvb = None
            if is_default and det and vd.startswith("accepted"):
                dvb = "same" if r["md5"] == c1["md5"] else "differs"
            if not opts.keep:
                for _ in range(5):
                    try:
                        if out.exists():
                            out.unlink()
                        break
                    except OSError:
                        time.sleep(0.2)
            return dict(row=name, value=v, verdict=vd, message=msg, rc=r["rc"], bytes=r["bytes"], ms=r["ms"],
                        paired=pair, is_default=is_default, default_vs_blank=dvb, cmd=" ".join(cmd(out, params)))
        except Exception as e:  # noqa: BLE001 - one broken run must not lose the list
            return dict(row=name, value=v, verdict="harness-error", message=f"{type(e).__name__}: {e}"[:200], rc=None,
                        bytes=0, ms=0, paired=pair, is_default=is_default, default_vs_blank=None, cmd=" ".join(cmd(out, params)))

    with ThreadPoolExecutor(max_workers=opts.jobs) as ex:
        entry["runs"] = list(ex.map(do, enumerate(tasks)))
    report.append(entry)
    n = len(entry["runs"])
    bad = sum(1 for r in entry["runs"] if r["verdict"] in BAD)
    print(f"  {kind}/{enc}: {n} runs, {n - bad} accepted, {bad} refused/dropped  ({entry['version']})", flush=True)


# ---- the report -------------------------------------------------------------------------------------
def write_report(report, outdir, src, opts):
    md = []
    md.append(f"# Encoder argument sweep - {time.strftime('%Y-%m-%d %H:%M')}\n")
    md.append(f"Source: `{src}`  \nBinaries: `{ENC_DIR}` and `{FFMPEG}`  \nJobs: {opts.jobs}, timeout {opts.timeout}s per run\n")
    tot_runs = tot_bad = 0
    for e in report:
        md.append(f"\n## {e['list']}\n")
        if e.get("error"):
            md.append(f"**Not swept:** {e['error']}\n")
        md.append(f"Version: `{e['version']}`  \nRows: {e['rows']}, values: {e['values']}"
                  + (f", blank control: {e['control_bytes']} bytes, deterministic: {e['deterministic']}" if e["deterministic"] is not None else "") + "\n")
        if e["skipped"]:
            md.append("Skipped rows: " + "; ".join(f"`{n}` ({why})" for n, why in e["skipped"]) + "\n")
        runs = e["runs"]
        if not runs:
            continue
        bad = [r for r in runs if r["verdict"] in BAD]
        msgs = [r for r in runs if r["verdict"] == "accepted-with-message"]
        paired = [r for r in runs if r["paired"]]
        dvb = [r for r in runs if r["default_vs_blank"] == "differs"]
        tot_runs += len(runs)
        tot_bad += len(bad)
        md.append(f"Runs: {len(runs)}, accepted: {len(runs) - len(bad)}, refused/dropped: {len(bad)}, paired: {len(paired)}\n")
        if bad:
            md.append("\n| row | value | verdict | paired | rc | bytes | message |\n|---|---|---|---|---|---|---|")
            for r in bad:
                p = f"{r['paired'][0]}={r['paired'][1]} (its default {r['paired'][2]})" if r["paired"] else ""
                md.append(f"| `{r['row']}` | `{r['value']}` | {r['verdict']} | {p} | {r['rc']} | {r['bytes']} | {r['message'].replace('|', '/')} |")
            md.append("")
        if paired:
            md.append("Paired runs (the partner moved with the value, as the record's re-check did): "
                      + ", ".join(f"`{r['row']}={r['value']}` with `{r['paired'][0]}={r['paired'][1]}` -> {r['verdict']}" for r in paired) + "\n")
        if msgs:
            md.append("Accepted with a message on stderr (read them; an error word in a normal log line lands here too):\n")
            for r in msgs:
                md.append(f"- `{r['row']}={r['value']}`: {r['message']}")
            md.append("")
        if e["deterministic"]:
            same = sum(1 for r in runs if r["default_vs_blank"] == "same")
            md.append(f"Stated default against a blank run: {same} identical" + (f", {len(dvb)} different - " + ", ".join(f"`{r['row']}={r['value']}`" for r in dvb) if dvb else "")
                      + ". A difference means the stated default is not the effective one at this preset, which the rows may say in words; it is information, not a fault.\n")
        elif e["deterministic"] is False:
            md.append("The encoder is not deterministic under these arguments, so the default-vs-blank comparison was not made.\n")
    md.append(f"\n## Totals\n\n{tot_runs} runs, {tot_bad} refused or dropped.\n")
    (outdir / "sweep-report.md").write_text("\n".join(md), encoding="utf-8")
    (outdir / "sweep-runs.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
    return tot_runs, tot_bad


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--enc", help="comma-separated list names (SvtAv1,AomAv1,Vpx,X264,X265 or the ffmpeg ones)")
    ap.add_argument("--only", help="comma-separated row names")
    ap.add_argument("--ffmpeg", action="store_true", help="sweep the encoderArgs/ffmpeg lists through ffmpeg instead")
    ap.add_argument("--gpu", action="store_true", help="include the NVENC lists (ask the user first)")
    ap.add_argument("--dry-run", action="store_true", help="extract and count, run nothing")
    ap.add_argument("--source", help="a y4m to encode (default: a generated 320x240 24 fps 3 s clip)")
    ap.add_argument("--out", default="sweep-out", help="output directory (default ./sweep-out)")
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--keep", action="store_true", help="keep every encode instead of deleting it after measuring")
    opts = ap.parse_args()
    opts.enc = set(opts.enc.split(",")) if opts.enc else None
    opts.only = set(opts.only.split(",")) if opts.only else None

    outdir = Path(winpath(opts.out))
    outdir.mkdir(parents=True, exist_ok=True)
    kind = "ffmpeg" if opts.ffmpeg else "av1an"
    table = FF if opts.ffmpeg else CLI
    names = [n for n in table if not opts.enc or n in opts.enc]
    if not names:
        sys.exit(f"no list selected; known: {', '.join(table)}")
    src = Path(winpath(opts.source)) if opts.source else (None if opts.dry_run else make_source(outdir))
    print(f"lists: {', '.join(names)}  source: {src}  binaries: {ENC_DIR if kind == 'av1an' else FFMPEG}", flush=True)

    report = []
    for enc in names:
        sweep_list(kind, enc, table[enc], src, outdir, opts, report)
        if not opts.dry_run:
            write_report(report, outdir, src, opts)  # after every list, so a crash keeps what ran
    if opts.dry_run:
        for e in report:
            print(f"\n{e['list']}: {e['rows']} rows, {e['values']} values" + (f"; skipped: {', '.join(n for n, _ in e['skipped'])}" if e["skipped"] else ""))
            for n, x in e.get("extraction", {}).items():
                print(f"  {n:28s} {' '.join(x['values'])}" + (f"   [default {x['default']}]" if x["default"] else ""))
        print(f"\ntotal values: {sum(e['values'] for e in report)}")
        return 0
    tot, bad = write_report(report, outdir, src, opts)
    print(f"\n{tot} runs, {bad} refused or dropped. Report: {outdir / 'sweep-report.md'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
