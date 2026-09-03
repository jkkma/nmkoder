#!/usr/bin/env bash
# make-fixture.sh - the named synthetic sources Nmkoder's measurements run against, one command each.
#
#   make-fixture.sh <shape|all> [outdir]          build into outdir (default: the current directory)
#   make-fixture.sh --check <shape|all> [outdir]  build, then re-probe the file and assert the properties
#                                                 the shape exists to have; exits 1 on any miss
#   make-fixture.sh --list                        the shapes, what each is for, and where the record uses it
#
# The record (CLAUDE.md and the reference skills) names these sources forty-odd times and never once
# as a command, so every harness re-derived them. Each recipe here was measured against the shipped
# ffmpeg (~/.nmkoder-dev/bin/ffmpeg.exe, BtbN N-126264-g007cd1fd43-20260825, the 2.8.78/2.8.79
# toolchain) on 2026-09-03, and --check is the same probes run again - so a later ffmpeg that quietly
# changes what a recipe produces fails here, in one line, rather than inside a harness three steps
# later. Run it after every toolchain refresh. FFMPEG=/path/to/ffmpeg overrides the binary; the
# ffprobe beside it is used. Fixtures belong in the scratchpad, never in the repo tree.
#
# Three things learned building it, each the kind of drift the record warns about:
#   - `-top 1` is gone from this ffmpeg ("Codec AVOption top ... is not a encoding option"), and
#     `-field_order tt` writes field_order=bb into MPEG-TS - the wrong parity, the same trap the
#     cadence-repair skill records for `-flags +ilme+ildct`. What tags a field correctly is the frame
#     property: the `setfield=tff` filter, and `tinterlace`, which sets it as it weaves.
#   - A frame-coded synthetic MPEG-2 does NOT reproduce a real capture's r_frame_rate=60000/1001
#     against avg_frame_rate=30000/1001 (CLAUDE.md, "Reading what the tools print"): ffmpeg's
#     mpeg2video writes frame pictures only, and that signature comes from field pictures. The
#     padded-cadence shape does show the disagreement - its jittered stamps push r_frame_rate above
#     avg_frame_rate the way a capture's field pictures do (48000/1001 against 30000/1001 here; the
#     figure follows the jitter pattern, and the check asserts only that the two differ) - so a test
#     of the GetFramerate rule uses that file, and the plain interlaced shape asserts r == avg in its
#     own check so nobody reads it as the capture case.
#   - Interlacing is a property of the picture, not of a tag. setfield alone on progressive frames had
#     idet call 49 of 90 frames TFF and 30 BFF; the shape here weaves a 59.94p source into 29.97i with
#     tinterlace, and idet then calls every frame TFF.
set -uo pipefail

SHAPES=(
  "interlaced|interlaced-29.97i.ts|720x480 29.97i MPEG-2 in MPEG-TS, TFF, real field motion (59.94p woven by tinterlace). Deinterlacing, InterlaceDetect, idet, QTGMC's source plugins. r == avg here - see the header."
  "padded-cadence|padded-cadence.ts|The interlaced shape with ~40% duplicate pictures and per-frame jittered stamps over the true 10 s: frame count 1.36x its length, r_frame_rate above avg_frame_rate (48000/1001 against 30000/1001 on this build). Cadence repair, GetFramerate, the refusal for an unpadded file. A model of a TBC's padding, not a measured capture."
  "keyframe-2s|keyframe-2s.mkv|640x360 24 fps H.264, 40 s, a keyframe exactly every 2 s (g 48, sc_threshold 0) with B-frames. The CRF ladder's pre-roll arithmetic, the stream-copy cut's two extra frames, keyframe trims."
  "scenecut-0|scenecut-0-pq.mkv|4 s dark PQ BT.2020 10-bit x265 clip with a 3-frame white event at EVENT_AT (default 1.5 s) and scenecut=0, keyint 48, so no keyframe lands on the event. The peak scan; sweep EVENT_AT across positions rather than placing it once."
  "hdr-pq|hdr-pq.mkv|640x360 PQ BT.2020 10-bit x265 with a mastering display (L 1000/0.0001) and MaxCLL 1000 / MaxFALL 400. ToneMapConfig, IsHdr, GetDeclaredPeakNits, the previews."
  "hdr-hlg|hdr-hlg.mkv|The same source tagged HLG (arib-std-b67), no mastering display. The HLG half of the tone-map chain."
  "anamorphic-16x9|anamorphic-16x9.mkv|720x480 H.264 with SAR 32:27 (DAR 16:9). The IVF/AV1 pixel-aspect loss, GetMuxAspectArgs, GetPipeSarFilter, the de-squeeze."
  "anamorphic-4x3|anamorphic-4x3.mkv|720x480 H.264 with SAR 8:9 (DAR 4:3), the NTSC capture shape the direct-encoders skill measured."
  "loudness-stereo|loudness-stereo.flac|20 s stereo sine: 0-10 s loud, 10-20 s 26 dB under it. Loudnorm one-pass versus two-pass, the trim going with the measurement."
  "loudness-5.1|loudness-5.1.flac|The same step in a 5.1 layout. The channel conversion inside the loudnorm filter, GetOutputChannelCount."
  "crf-ladder|crf-ladder-3min.mkv|640x360 24 fps H.264, three minutes whose content changes every minute (testsrc2, mandelbrot, life), keyframe every 2 s. The Sample Encodes utility's three samples landing on different material."
  "y4m-small|y4m-small.y4m|320x240 24 fps 8-bit 4:2:0 y4m, 3 s (72 frames). The direct-encoder chain and the argument-list sweep; the source the record's own sweeps used."
)

usage() { sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; }
list() { for s in "${SHAPES[@]}"; do IFS='|' read -r n f d <<<"$s"; printf '%-16s %-24s %s\n' "$n" "$f" "$d"; done; }

CHECK=0
case "${1:-}" in
  --check) CHECK=1; shift ;;
  --list) list; exit 0 ;;
  -h|--help|"") usage; exit 0 ;;
esac
SHAPE="$1"; OUT="${2:-.}"
mkdir -p "$OUT"

if [ -z "${FFMPEG:-}" ]; then
  for c in "$HOME/.nmkoder-dev/bin/ffmpeg.exe" "$HOME/.nmkoder-dev/bin/ffmpeg"; do [ -x "$c" ] && FFMPEG="$c" && break; done
  [ -n "${FFMPEG:-}" ] || FFMPEG="$(command -v ffmpeg || true)"
fi
[ -n "${FFMPEG:-}" ] && [ -x "$FFMPEG" ] || { echo "no ffmpeg found (set FFMPEG=)" >&2; exit 1; }
FFPROBE="${FFMPEG%ffmpeg*}ffprobe${FFMPEG##*ffmpeg}"
[ -x "$FFPROBE" ] || { echo "no ffprobe beside $FFMPEG" >&2; exit 1; }
echo "using: $FFMPEG ($("$FFMPEG" -version 2>/dev/null | head -1 | cut -d' ' -f3))"

ff() { "$FFMPEG" -hide_banner -v error -y "$@"; }
fp() { "$FFPROBE" -v error "$@"; }
# One stream entry per call, so ffprobe's own field order and its trailing comma never reach a
# check - and the first line only: MPEG-TS and PS carry a program section, and ffprobe prints a
# stream entry once at the top level and once again under the program, blank line between. The
# Windows ffprobe ends every line CRLF, so the CR goes with the comma.
sv() { fp -select_streams v:0 -show_entries "stream=$2" -of csv=p=0 "$1" | head -n1 | tr -d ',\r'; }
sa() { fp -select_streams a:0 -show_entries "stream=$2" -of csv=p=0 "$1" | head -n1 | tr -d ',\r'; }
fmt() { fp -show_entries "format=$2" -of csv=p=0 "$1" | head -n1 | tr -d ',\r'; }
nframes() { fp -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 "$1" | head -n1 | tr -d ',\r'; }
frame_tags() { fp -select_streams v:0 -show_entries "frame=$2" -of csv=p=0 -read_intervals '%+#10' "$1" | tr -d ',\r' | grep -v '^$' | sort -u | tr '\n' ' ' | sed 's/ $//'; }
keyframes() { fp -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 "$1" | tr -d ',\r' | tr '\n' ' '; }
even_seconds() { echo "$1" | awk -v min="$2" '{ if (NF<min) exit 1; for (i=1;i<=NF;i++) { v=$i+0; d=v-2*int(v/2); if (d>0.001 || d<-0.001) exit 1 } }'; }

PASS=0; FAILN=0
ok()   { PASS=$((PASS+1)); echo "  ok   $1"; }
fail() { FAILN=$((FAILN+1)); echo "  FAIL $1"; }
assert_eq() { if [ "$2" = "$3" ]; then ok "$1: $2"; else fail "$1: got '$2', wanted '$3'"; fi; }

# ---- interlaced ------------------------------------------------------------------------------------
build_interlaced() {
  ff -f lavfi -i "testsrc2=size=720x480:rate=60000/1001" -t 4 -vf "tinterlace=mode=interleave_top,setfield=tff" \
     -c:v mpeg2video -flags +ilme+ildct -alternate_scan 1 -q:v 4 -pix_fmt yuv420p -muxdelay 0 "$1"
}
idet_counts() { "$FFMPEG" -hide_banner -i "$1" -vf idet -f null - 2>&1 | grep 'Multi frame detection' | tail -1 \
  | sed 's/.*TFF: *\([0-9]*\).*BFF: *\([0-9]*\).*Progressive: *\([0-9]*\).*/\1 \2 \3/'; }
check_interlaced() {
  assert_eq "field_order" "$(sv "$1" field_order)" "tt"
  assert_eq "r_frame_rate (frame-coded: same as avg, unlike a real capture)" "$(sv "$1" r_frame_rate)" "30000/1001"
  assert_eq "avg_frame_rate" "$(sv "$1" avg_frame_rate)" "30000/1001"
  assert_eq "frames interlaced_frame" "$(frame_tags "$1" interlaced_frame)" "1"
  assert_eq "frames top_field_first" "$(frame_tags "$1" top_field_first)" "1"
  set -- $(idet_counts "$1")
  if [ "${1:-0}" -gt 0 ] && [ "${2:-1}" -eq 0 ] && [ "${3:-1}" -eq 0 ]; then ok "idet: TFF $1, BFF $2, progressive $3"; else fail "idet: TFF ${1:-?}, BFF ${2:-?}, progressive ${3:-?}"; fi
}

# ---- padded-cadence --------------------------------------------------------------------------------
build_padded_cadence() {
  local out=$1 tmp="$OUT/.fx-pad.$$" n=0 m=0 lcg=42 f
  rm -rf "$tmp"; mkdir -p "$tmp"
  ff -f lavfi -i "testsrc2=size=720x480:rate=60000/1001" -t 10 -vf "tinterlace=mode=interleave_top" "$tmp/f%05d.png"
  : > "$tmp/list.txt"
  # Every picture once, and again 40% of the time, decided by a fixed LCG rather than $RANDOM so the
  # file is identical on every machine and every bash.
  for f in "$tmp"/f*.png; do
    n=$((n+1)); m=$((m+1)); printf "file '%s'\n" "$(basename "$f")" >> "$tmp/list.txt"
    lcg=$(( (lcg * 1103515245 + 12345) % 2147483648 ))
    if [ $(( (lcg >> 8) % 100 )) -lt 40 ]; then m=$((m+1)); printf "file '%s'\n" "$(basename "$f")" >> "$tmp/list.txt"; fi
  done
  # A constant-rate encode of the padded sequence, no B-frames so pts == dts and one setts moves both...
  ff -f concat -safe 0 -r 30000/1001 -i "$tmp/list.txt" -vf setfield=tff -c:v mpeg2video -flags +ilme+ildct \
     -alternate_scan 1 -bf 0 -q:v 4 -pix_fmt yuv420p -muxdelay 0 "$tmp/cfr.ts"
  # ...then the stamps rewritten at the container: m pictures spread over the true n/29.97 s, plus a
  # +-8 ms jitter derived from the picture index, in 90 kHz ticks. The mpeg2video encoder cannot write
  # these itself - its timebase is the frame rate, so a VFR source through it died with EINVAL.
  ff -i "$tmp/cfr.ts" -c copy -bsf:v "setts=ts='floor(N*3003*$n/$m + (mod(N*7919\,17)-8)*90)'" -muxdelay 0 "$out"
  rm -rf "$tmp"
  echo "  $n real pictures, $m coded ($(awk -v n=$n -v m=$m 'BEGIN{printf "%.2f", m/n}')x)"
}
check_padded_cadence() {
  local n dur ratio r a
  n=$(nframes "$1")
  dur=$(fmt "$1" duration)
  ratio=$(awk -v n="$n" -v d="$dur" 'BEGIN{printf "%.4f", n/(d*30000/1001)}')
  if awk -v r="$ratio" 'BEGIN{exit !(r>1.3)}'; then ok "coded frames / (duration x 29.97) = $ratio ($n frames over ${dur}s)"; else fail "ratio $ratio, wanted > 1.3"; fi
  r=$(sv "$1" r_frame_rate); a=$(sv "$1" avg_frame_rate)
  if [ "$r" != "$a" ]; then ok "r_frame_rate $r against avg_frame_rate $a - the capture signature GetFramerate guards"; else fail "r_frame_rate == avg_frame_rate == $r"; fi
  assert_eq "field_order" "$(sv "$1" field_order)" "tt"
}

# ---- keyframe-2s -----------------------------------------------------------------------------------
build_keyframe_2s() { ff -f lavfi -i "testsrc2=size=640x360:rate=24" -t 40 -c:v libx264 -preset veryfast -g 48 -keyint_min 48 -sc_threshold 0 -bf 3 -pix_fmt yuv420p "$1"; }
check_keyframe_2s() {
  local kf b; kf="$(keyframes "$1")"
  if even_seconds "$kf" 20; then ok "keyframes on even seconds only ($(echo $kf | wc -w) of them over 40 s)"; else fail "keyframes: $kf"; fi
  b=$(fp -select_streams v:0 -show_entries frame=pict_type -of csv=p=0 -read_intervals '%+#100' "$1" | grep -c B)
  if [ "$b" -gt 0 ]; then ok "B-frames present ($b of the first 100)"; else fail "no B-frames"; fi
}

# ---- scenecut-0 ------------------------------------------------------------------------------------
MD_4000="G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(40000000,50)"
MD_1000="G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)"
PQ_TAGS="-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc"
build_scenecut_0() {
  local at="${EVENT_AT:-1.5}" end; end=$(awk -v a="$at" 'BEGIN{printf "%.4f", a+0.125}')
  ff -f lavfi -i "color=c=0x101010:size=640x360:rate=24" -t 4 \
     -vf "drawbox=x=160:y=90:w=320:h=180:color=white:t=fill:enable='gte(t,$at)*lt(t,$end)',format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv" \
     -c:v libx265 -preset ultrafast -x265-params "scenecut=0:keyint=48:min-keyint=48:master-display=$MD_4000:max-cll=1000,400:hdr10=1:repeat-headers=1" \
     $PQ_TAGS "$1"
  echo "  event at ${at}s (frames $(awk -v a="$at" 'BEGIN{printf "%d", a*24+0.5}') to $(awk -v a="$at" 'BEGIN{printf "%d", a*24+2.5}'))"
}
check_scenecut_0() {
  local at="${EVENT_AT:-1.5}" idx kf lum; idx=$(awk -v a="$at" 'BEGIN{printf "%d", a*24+0.5}')
  assert_eq "transfer" "$(sv "$1" color_transfer)" "smpte2084"
  kf="$(keyframes "$1")"
  if even_seconds "$kf" 2; then ok "keyframes on even seconds only: $kf"; else fail "keyframes: $kf"; fi
  if echo "$kf" | awk -v i="$idx" '{for(k=1;k<=NF;k++) if (int($k*24+0.5)>=i && int($k*24+0.5)<=i+2) exit 1}'; then ok "no keyframe on the event (frames $idx-$((idx+2)))"; else fail "a keyframe landed on the event"; fi
  lum="$("$FFMPEG" -hide_banner -v error -i "$1" -vf "select='between(n,$((idx-2)),$((idx+4)))',signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -fps_mode passthrough -f null - 2>/dev/null | grep -o 'YAVG=[0-9.]*' | cut -d= -f2 | tr '\n' ' ')"
  if echo "$lum" | awk '{ if (NF<7) exit 1; if ($1>200||$2>200||$6>200||$7>200) exit 1; if ($3<250||$4<250||$5<250) exit 1 }'; then ok "event frames bright, neighbours dark (YAVG of 10-bit: $lum)"; else fail "YAVG around the event: $lum"; fi
}

# ---- hdr-pq / hdr-hlg ------------------------------------------------------------------------------
build_hdr_pq() {
  ff -f lavfi -i "testsrc2=size=640x360:rate=24" -t 4 -pix_fmt yuv420p10le -vf "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv" \
     -c:v libx265 -preset ultrafast -x265-params "master-display=$MD_1000:max-cll=1000,400:hdr10=1:repeat-headers=1" $PQ_TAGS "$1"
}
side_data() { fp -select_streams v:0 -show_frames -read_intervals '%+#1' -of json "$1"; }
check_hdr_pq() {
  assert_eq "transfer" "$(sv "$1" color_transfer)" "smpte2084"
  assert_eq "primaries" "$(sv "$1" color_primaries)" "bt2020"
  assert_eq "pix_fmt" "$(sv "$1" pix_fmt)" "yuv420p10le"
  local sd; sd="$(side_data "$1")"
  if echo "$sd" | grep -q "Mastering display metadata"; then ok "mastering display side data present"; else fail "no mastering display side data"; fi
  if echo "$sd" | grep -q '"max_content": 1000'; then ok "content light level MaxCLL 1000"; else fail "no MaxCLL 1000 in side data"; fi
}
build_hdr_hlg() {
  ff -f lavfi -i "testsrc2=size=640x360:rate=24" -t 4 -pix_fmt yuv420p10le -vf "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv" \
     -c:v libx265 -preset ultrafast -x265-params "repeat-headers=1" -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc "$1"
}
check_hdr_hlg() {
  assert_eq "transfer" "$(sv "$1" color_transfer)" "arib-std-b67"
  assert_eq "primaries" "$(sv "$1" color_primaries)" "bt2020"
  if side_data "$1" | grep -q "Mastering display metadata"; then fail "HLG shape carries a mastering display"; else ok "no mastering display side data"; fi
}

# ---- anamorphic ------------------------------------------------------------------------------------
build_anamorphic() { ff -f lavfi -i "testsrc2=size=720x480:rate=30000/1001" -t 4 -vf "setsar=$2" -c:v libx264 -preset veryfast -pix_fmt yuv420p "$1"; }
build_anamorphic_16x9() { build_anamorphic "$1" 32/27; }
build_anamorphic_4x3()  { build_anamorphic "$1" 8/9; }
check_anamorphic_16x9() { assert_eq "sample_aspect_ratio" "$(sv "$1" sample_aspect_ratio)" "32:27"; assert_eq "display_aspect_ratio" "$(sv "$1" display_aspect_ratio)" "16:9"; }
check_anamorphic_4x3()  { assert_eq "sample_aspect_ratio" "$(sv "$1" sample_aspect_ratio)" "8:9";   assert_eq "display_aspect_ratio" "$(sv "$1" display_aspect_ratio)" "4:3"; }

# ---- loudness --------------------------------------------------------------------------------------
build_loudness() { ff -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 20 -af "volume=volume=0.05:enable='gte(t,10)',$2" -c:a flac "$1"; }
build_loudness_stereo() { build_loudness "$1" "aformat=channel_layouts=stereo"; }
build_loudness_5_1()    { build_loudness "$1" "pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0"; }
lufs() { "$FFMPEG" -hide_banner -nostats -ss "$2" -t 10 -i "$1" -af ebur128=framelog=quiet -f null - 2>&1 | tr -d '\r' | grep -A1 'Integrated loudness' | grep -o 'I: *-\?[0-9.]*' | awk '{print $2}'; }
check_loudness_step() {
  local a b d; a=$(lufs "$1" 0); b=$(lufs "$1" 10); d=$(awk -v a="$a" -v b="$b" 'BEGIN{printf "%.1f", a-b}')
  if awk -v d="$d" 'BEGIN{exit !(d>24 && d<28)}'; then ok "first half $a LUFS, second $b LUFS: $d LU apart"; else fail "halves $a / $b LUFS, $d LU apart (wanted ~26)"; fi
}
check_loudness_stereo() { assert_eq "channel_layout" "$(sa "$1" channel_layout)" "stereo"; check_loudness_step "$1"; }
check_loudness_5_1()    { assert_eq "channel_layout" "$(sa "$1" channel_layout)" "5.1";    check_loudness_step "$1"; }

# ---- crf-ladder ------------------------------------------------------------------------------------
build_crf_ladder() {
  ff -f lavfi -i "testsrc2=size=640x360:rate=24" -f lavfi -i "mandelbrot=size=640x360:rate=24" \
     -f lavfi -i "life=size=640x360:rate=24:mold=10:ratio=0.1:death_color=#C83232:life_color=#00ff00" \
     -filter_complex "[0:v]trim=duration=60,setpts=PTS-STARTPTS[a];[1:v]trim=duration=60,setpts=PTS-STARTPTS[b];[2:v]trim=duration=60,setpts=PTS-STARTPTS[c];[a][b][c]concat=n=3:v=1:a=0,format=yuv420p[v]" \
     -map "[v]" -c:v libx264 -preset veryfast -g 48 -keyint_min 48 -sc_threshold 0 "$1"
}
md5at() { "$FFMPEG" -hide_banner -v error -ss "$2" -i "$1" -frames:v 1 -f framemd5 - 2>/dev/null | tr -d '\r' | grep -v '^#' | awk '{print $NF}'; }
check_crf_ladder() {
  assert_eq "duration" "$(fmt "$1" duration)" "180.000000"
  local kf; kf="$(keyframes "$1")"
  if even_seconds "$kf" 90; then ok "keyframes on even seconds only ($(echo $kf | wc -w) over 180 s)"; else fail "keyframes: $(echo $kf | cut -c1-80)..."; fi
  local a b c; a=$(md5at "$1" 10); b=$(md5at "$1" 70); c=$(md5at "$1" 130)
  if [ -n "$a" ] && [ "$a" != "$b" ] && [ "$b" != "$c" ] && [ "$a" != "$c" ]; then ok "frames at 10 s, 70 s and 130 s all differ (three sources)"; else fail "segment frames not distinct: $a $b $c"; fi
}

# ---- y4m-small -------------------------------------------------------------------------------------
build_y4m_small() { ff -f lavfi -i "testsrc2=size=320x240:rate=24" -t 3 -pix_fmt yuv420p "$1"; }
check_y4m_small() {
  local h; h="$(head -c 80 "$1" | head -n1)"
  if echo "$h" | grep -q "W320 H240 F24:1" && echo "$h" | grep -q "C420"; then ok "header: $h"; else fail "header: $h"; fi
  assert_eq "frames" "$(nframes "$1")" "72"
}

# ---- dispatch --------------------------------------------------------------------------------------
fn_of() { printf '%s' "$1" | tr '.-' '__'; }
run_shape() {
  local name=$1 file="" s
  for s in "${SHAPES[@]}"; do IFS='|' read -r n f d <<<"$s"; [ "$n" = "$name" ] && file="$f"; done
  [ -n "$file" ] || { echo "unknown shape: $name (see --list)" >&2; exit 2; }
  local path="$OUT/$file" fn; fn="$(fn_of "$name")"
  echo "== $name -> $path"
  if ! "build_$fn" "$path" || [ ! -s "$path" ]; then fail "$name: not built"; return; fi
  echo "  built: $(stat -c %s "$path") bytes"
  [ "$CHECK" -eq 1 ] && "check_$fn" "$path"
}
if [ "$SHAPE" = "all" ]; then for s in "${SHAPES[@]}"; do IFS='|' read -r n f d <<<"$s"; run_shape "$n"; done; else run_shape "$SHAPE"; fi
if [ "$CHECK" -eq 1 ]; then echo "checks: $PASS ok, $FAILN failed"; [ "$FAILN" -eq 0 ] || exit 1; fi
exit 0
