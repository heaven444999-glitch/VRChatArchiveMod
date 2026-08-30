# Live chatbox probe: sends measurement patterns to VRChat's OSC input
# (/chatbox/input, UDP 127.0.0.1:9000) to size the bubble for the Bad Apple grid.
#
# Commands:
#   ruler   30 CJK numerals in one unbroken line ("一二三...十" x3): the position
#           where the bubble wraps line 1 = max full-width glyphs per line.
#   grid    144 chars as a repeating 12-glyph pattern, no newlines: if the bubble
#           wraps at exactly 12, all rows come out identical and column-aligned;
#           any drift between rows reveals the true wrap width (and a fully shown
#           message proves 144 chars are accepted).
#   frame N send baked Bad Apple frame at N seconds (from ressources/badapple.frames)
#   play [ms] [typing]
#           play the WHOLE video in the chatbox (default cadence 200 ms).
#           "typing" = populate the keyboard instead of sending: the live typing
#           preview bypasses the sent-message spam filter (fast cadences).
#           Ctrl+C stops and clears the bubble. Standalone test of exactly what
#           the mod's Right-Shift+B does, without needing the DLL loaded.
#   text S  send arbitrary text
#   clear   empty the bubble
#
# Usage: python tools/chatbox_probe.py play|ruler|grid|clear|frame <sec>|text <msg>

import io
import os
import socket
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
FRAMES = os.path.join(HERE, "..", "ressources", "badapple.frames")
CHARSET = os.path.join(HERE, "..", "ressources", "badapple.charset")


def osc_string(s):
    b = s.encode("utf-8")
    return b + b"\x00" * (4 - len(b) % 4)


def send(text, immediate=True):
    # immediate=False populates the in-game keyboard instead of sending: VRChat
    # broadcasts the live typing preview WITHOUT the sent-message spam filter,
    # which is how sub-500 ms cadences survive.
    tags = ",sTF" if immediate else ",sFF"
    packet = osc_string("/chatbox/input") + osc_string(tags) + osc_string(text)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.sendto(packet, ("127.0.0.1", 9000))
    sock.close()
    print(f"sent {len(text)} chars{'' if immediate else ' (typing)'}")


def load_baked():
    with io.open(CHARSET, encoding="utf-8") as f:
        charset = f.read().rstrip("\r\n")
    with io.open(FRAMES, encoding="ascii") as f:
        header = f.readline().split()
        w, h, base = int(header[0]), int(header[1]), int(header[2])
        joiner = "\n" if len(header) < 4 or header[3] != "0" else ""
        frames = [line.strip() for line in f if line.strip()]
    return charset, w, h, base, joiner, frames


DIGITS = "0123456789abcdefghijklmnopqrstuv"


def render(frame, charset, w, h, joiner):
    return joiner.join(
        "".join(charset[(DIGITS.index(c) * (len(charset) - 1) + 15) // 31]
                for c in frame[r * w:(r + 1) * w])
        for r in range(h))


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "ruler"
    if cmd == "ladder":
        # Escalating lengths, 3 s apart. Each message = its own length in full-width
        # digits + CJK numeral filler. Messages over the game's max are dropped, so
        # the bubble ends up showing the LARGEST accepted size as its label — and
        # where its first line wraps = glyphs per line.
        fw = str.maketrans("0123456789", "０１２３４５６７８９")
        for n in (144, 160, 176, 192, 208, 224, 240, 256):
            label = str(n).translate(fw)
            filler = ("一二三四五六七八九十" * 26)[:n - len(label)]
            send(label + filler)
            time.sleep(3)
    elif cmd == "ratetest":
        # No local channel exposes the chatbox rate limit (log is silent, OSCQuery has
        # no metadata), so this measures it with eyes: 7 stages, 8 s each, realistic
        # 180-char messages. Every update flips the whole bubble between a 疆-wall and
        # a ・-wall, with the stage cadence + counter as the top row. The last cadence
        # where the flip still tracks every update = the real limit. 3 s cooldown
        # between stages so a tripped spam filter doesn't bleed into the next one.
        fw = str.maketrans("0123456789", "０１２３４５６７８９")
        for cad in (500, 400, 300, 250, 200, 150, 100):
            print(f"palier {cad} ms...")
            n = max(1, 8000 // cad)
            start = time.monotonic()
            for i in range(n):
                head = (f"{cad}".translate(fw) + "＃" + f"{i:02d}".translate(fw))
                head += "・" * (15 - len(head))
                body = ("疆" if i % 2 == 0 else "・") * 165
                send(head + body)
                wait = start + (i + 1) * cad / 1000 - time.monotonic()
                if wait > 0:
                    time.sleep(wait)
            time.sleep(3)
        send("")
        print("fini — quel palier a freeze en premier ?")
    elif cmd == "blanktest":
        # VRChat collapses runs of real whitespace (U+3000 included) in the chatbox,
        # so level 0 needs a NON-space blank. Three candidates, 4 rows each:
        #   rows 1-4  : U+3164 Hangul Filler (fully invisible if supported)
        #   rows 5-8  : U+2800 Braille blank (invisible, width uncertain)
        #   rows 9-12 : U+30FB katakana middle dot (faint but guaranteed full-width)
        # Whichever section keeps its 疆 columns aligned wins.
        out = []
        for ch in ("ㅤ", "⠀", "・"):
            out += [("疆" + ch) * 7 + "疆"] * 4
        send("".join(out))
    elif cmd == "ruler":
        send("一二三四五六七八九十" * 3)
    elif cmd == "grid":
        send("一二三四五六七八九十甲乙" * 12)
    elif cmd == "clear":
        send("")
    elif cmd == "text":
        send(sys.argv[2])
    elif cmd == "frame":
        sec = float(sys.argv[2]) if len(sys.argv) > 2 else 100
        charset, w, h, base, joiner, frames = load_baked()
        idx = min(int(sec * 1000 / base), len(frames) - 1)
        send(render(frames[idx], charset, w, h, joiner))
    elif cmd == "play":
        interval = int(sys.argv[2]) if len(sys.argv) > 2 else 200
        typing = len(sys.argv) > 3 and sys.argv[3].lower().startswith("typ")
        charset, w, h, base, joiner, frames = load_baked()
        total = len(frames) * base / 1000
        print(f"BAD APPLE -> chatbox : {w}x{h}, cadence {interval} ms, "
              f"mode {'TYPING (apercu de frappe)' if typing else 'SEND'}, "
              f"duree {int(total // 60)}:{int(total % 60):02d} - Ctrl+C pour arreter")
        try:
            while True:  # loop the whole PV until Ctrl+C, like the mod's Loop default
                start = time.monotonic()
                i = 0
                while True:
                    elapsed = i * interval
                    idx = (elapsed + base // 2) // base
                    if idx >= len(frames):
                        break
                    send(render(frames[idx], charset, w, h, joiner), immediate=not typing)
                    i += 1
                    wait = start + i * interval / 1000 - time.monotonic()
                    if wait > 0:
                        time.sleep(wait)
        except KeyboardInterrupt:
            print("\nstop.")
        finally:
            send("")
            print("bulle videe.")
    else:
        sys.exit(f"unknown command {cmd}")


if __name__ == "__main__":
    main()
