# Bakes the Bad Apple!! MP4 into a compact text-frame file embedded into the mod DLL
# (ressources/badapple.frames), for the chatbox-OSC player (BadAppleModule).
#
# Frame format (all constraints come from the VRChat chatbox):
#   - measured in-game (chatbox_probe.py, 2026-08-20): the bubble wraps at exactly
#     15 full-width glyphs per line and a 180-char message displays untruncated ->
#     default 15x12 grid, no newlines (natural wrap), 180 chars per message
#   - frames are baked DENSE (50 ms base) and the mod samples them at the configured
#     playback cadence (default 500 ms, validated in-game 2026-08-20; ~450 ms observed
#     working elsewhere — the spam filter runs on the RECEIVING clients, so empirical).
#     50 divides every 50 ms-multiple cadence, so sampling never jitters.
#   - 32 grey levels, base-36 digit 0 = lightest .. v = darkest; the mod maps them
#     onto the measured hanzi brightness ramp (tools/pick_charset.py ->
#     ressources/badapple.charset).
#
# Output file:
#   line 1: "<width> <height> <interval_ms>"
#   then one line per frame: width*height base-36 digits, row-major.
#
# Usage:  python tools/bake_badapple.py  [--width 10 --height 7 --interval-ms 250]

import argparse
import os
import shutil
import sys
import tempfile

import cv2

HERE = os.path.dirname(os.path.abspath(__file__))
LINE_ASPECT = 1.15  # chatbox line-height relative to a full-width glyph's width
DEFAULT_VIDEO = os.path.join(HERE, "..", "ressources",
                             "【東方】Bad Apple!! ＰＶ【影絵】.mp4")
DEFAULT_OUT = os.path.join(HERE, "..", "ressources", "badapple.frames")
CHARSET_FILE = os.path.join(HERE, "..", "ressources", "badapple.charset")
LEVELS = 32
DIGITS = "0123456789abcdefghijklmnopqrstuv"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", default=DEFAULT_VIDEO)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--width", type=int, default=15)
    ap.add_argument("--height", type=int, default=12)
    ap.add_argument("--interval-ms", type=int, default=50)
    ap.add_argument("--fit", choices=["crop", "stretch"], default="crop",
                    help="crop = center-crop the video so the grid keeps true proportions; "
                         "stretch = use the full 4:3 image distorted to the grid's aspect")
    ap.add_argument("--sep", choices=["newline", "none"], default="none",
                    help="none = no newlines, rely on the bubble's natural wrap (width must "
                         "equal the measured wrap width exactly); frees ~height-1 chars")
    ap.add_argument("--max-chars", type=int, default=180,
                    help="chatbox message limit measured in-game (chatbox_probe.py ladder)")
    args = ap.parse_args()

    sep = 1 if args.sep == "newline" else 0
    chars_per_msg = args.width * args.height + (args.height - 1) * sep
    if chars_per_msg > args.max_chars:
        sys.exit(f"grid {args.width}x{args.height} = {chars_per_msg} chars > {args.max_chars} limit")

    # cv2.VideoCapture chokes on non-ASCII paths on Windows: open via an ASCII temp copy.
    tmp = os.path.join(tempfile.gettempdir(), "badapple_bake_src.mp4")
    shutil.copyfile(args.video, tmp)
    cap = cv2.VideoCapture(tmp)
    if not cap.isOpened():
        sys.exit("could not open video")

    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    step = args.interval_ms / 1000.0 * fps  # source frames per baked frame

    frames = []
    next_take = 0.0
    idx = 0
    while True:
        ok = cap.grab()
        if not ok:
            break
        if idx >= next_take:
            ok, img = cap.retrieve()
            if not ok:
                break
            gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
            if args.fit == "crop":
                sh, sw = gray.shape
                target = args.width / (args.height * LINE_ASPECT)  # aspect as displayed
                if sw / sh > target:      # source wider than the grid -> trim the sides
                    keep = round(sh * target)
                    gray = gray[:, (sw - keep) // 2:(sw - keep) // 2 + keep]
                elif sw / sh < target:    # source taller -> trim top/bottom
                    keep = round(sw / target)
                    gray = gray[(sh - keep) // 2:(sh - keep) // 2 + keep, :]
            small = cv2.resize(gray, (args.width, args.height), interpolation=cv2.INTER_AREA)
            # highest level = darkest pixel = densest glyph (the silhouette is what gets drawn)
            levels = (LEVELS - 1 - (small.astype(int) * LEVELS // 256)).clip(0, LEVELS - 1)
            frames.append("".join(DIGITS[d] for d in levels.flatten()))
            next_take += step
        idx += 1
    cap.release()
    os.remove(tmp)

    with open(args.out, "w", encoding="ascii", newline="\n") as f:
        f.write(f"{args.width} {args.height} {args.interval_ms} {sep}\n")
        f.write("\n".join(frames) + "\n")

    dur = len(frames) * args.interval_ms / 1000.0
    print(f"baked {len(frames)} frames ({args.width}x{args.height}, {args.interval_ms} ms) "
          f"-> {dur:.0f}s playback, {os.path.getsize(args.out)} bytes")
    # eyeball check: dump a mid-video frame as it will look in the chatbox
    with open(CHARSET_FILE, encoding="utf-8") as f:
        charset = f.read().rstrip("\r\n")  # not strip(): level 0 is U+3000, a "space"
    mid = frames[len(frames) // 2]
    for r in range(args.height):
        print(">" + "".join(charset[(DIGITS.index(c) * (len(charset) - 1) + (LEVELS - 1) // 2) // (LEVELS - 1)]
                            for c in mid[r * args.width:(r + 1) * args.width]))


if __name__ == "__main__":
    main()
