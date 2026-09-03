#!/usr/bin/env bash
# PreToolUse(Bash|PowerShell) hook: ask before a command that would hold the GPU.
#
# The rule is the user's, stated 17 August 2026: nothing that loads the GPU on the machine the
# session runs on - an NVENC encode, a libplacebo or Vulkan render past a probe frame, a Vship
# CUDA metric run, an av1an or Quick Convert run with a GPU stage in the chain - starts without
# asking first, because the user is at the machine and a GPU pinned by a background measurement
# takes the display with it. The CPU half of a task goes first; then one line naming what would
# run and roughly how long it holds the GPU; then the rest once they say yes. The rule lived in
# a memory file, which a session has to recall; this applies it whether or not one did, and it
# fires for a subagent's commands too, since those pass through the same event. The user-scope
# screen-capture hook is the model: one grep over the command, one line of JSON.
#
# Two conditions, both required. A GPU word - the NVENC/NVDEC encoders, libplacebo, Vship, CUDA,
# Vulkan, a hwaccel - and a launcher that can reach one: ffmpeg or ffplay, av1an, VSPipe, python
# (Vship is a VapourSynth plugin), `dotnet run`, or any .exe. The second condition is what keeps
# this out of the way of ordinary work on this project, where `grep -rn nvenc Nmkoder` is a
# normal thing to type and mentions the GPU eighty-eight times without touching it. Words rather
# than flags, on purpose: the same GPU is reached as `-c:v hevc_nvenc`, as `--encoder nvenc`
# inside a harness's argument string and as a bare `hevc_nvenc` handed to a launched exe, and a
# hook that parsed flags would see the first only. It stands down for the cheap probe the rule
# exempts: a command that is also listing encoders, decoders, filters, hwaccels or the build
# configuration, or asking for --help, -version or `-h encoder=`, is asking what exists rather
# than running it.
#
# Limits, stated: it sees the command text and nothing else. A compiled harness whose GPU work is
# in its source rather than on its command line, a VapourSynth script invoked by file name with
# Vship inside it, and an app launch that reaches NVENC through the UI are all outside it - which
# is why the rule itself stays in force and this is a backstop, not a substitute for reading it.
# It asks rather than denies, because the answer is the user's to give; every path exits 0.
# Verified against crafted tool inputs under Git Bash - see .claude/README.md for the cases.
set -uo pipefail

INPUT=""
[ -t 0 ] || INPUT="$(cat 2>/dev/null || true)"
[ -n "$INPUT" ] || exit 0

# The command as one JSON string, escapes and all: (\\.|[^"\\])* reads past an escaped quote.
CMD="$(printf '%s' "$INPUT" | grep -o -E '"command"[[:space:]]*:[[:space:]]*"(\\.|[^"\\])*"' | head -n1)"
[ -n "$CMD" ] || exit 0

# A probe, not a run.
printf '%s' "$CMD" | grep -q -i -E '(^|[^[:alnum:]_])(-encoders|-decoders|-filters|-buildconf|-hwaccels|-h[[:space:]]+(encoder|decoder|filter)|--help|-version)([^[:alnum:]_]|$)' && exit 0
# A GPU word...
printf '%s' "$CMD" | grep -q -i -E 'nvenc|nvdec|libplacebo|vship|cuda|vulkan|hwaccel' || exit 0
# ...and something that can reach the GPU with it.
printf '%s' "$CMD" | grep -q -i -E 'ffmpeg|ffplay|av1an|vspipe|python|dotnet[[:space:]]+run|\.exe' || exit 0

printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"This command looks like it would hold the GPU - NVENC, libplacebo, Vship, or a CUDA/Vulkan device, launched by ffmpeg, av1an, VSPipe, python, dotnet run or an exe. The standing rule (17 August 2026, the ask-before-gpu-stress memory) is to ask first: do the CPU half, then name what runs and roughly how long it takes the GPU, and go once the user says yes. A one-off probe is exempt - if that is what this is, answer yes."}}\n'
exit 0
