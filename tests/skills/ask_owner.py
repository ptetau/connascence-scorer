#!/usr/bin/env python3
"""Simulated product owner for interview evals.

Usage: ask_owner.py <config.json> "questions..."   (or questions on stdin)
config.json: {"persona": path, "log": path, "model": "haiku" | null}

Answers in character from the persona, using a separate model call, and appends each exchange to the log.
"""
import json
import os
import subprocess
import sys

STRIP_ENV = ["CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
             "CLAUDE_CODE_TEE_SDK_STDOUT", "CLAUDE_CODE_DIAGNOSTICS_FILE", "CLAUDE_CODE_ENTRYPOINT"]


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: ask-owner \"your questions\"")
    cfg = json.load(open(sys.argv[1]))
    q = " ".join(sys.argv[2:]).strip() or sys.stdin.read().strip()
    if not q:
        sys.exit("usage: ask-owner \"your questions\"")
    persona = open(cfg["persona"]).read()
    history = open(cfg["log"]).read() if os.path.exists(cfg["log"]) else ""
    prompt = ("You are role-playing a product owner being interviewed about their software. Stay in character.\n\n"
              "<persona>\n" + persona + "\n</persona>\n\n"
              "<conversation_so_far>\n" + (history or "(none)") + "\n</conversation_so_far>\n\n"
              "<new_message_from_interviewer>\n" + q + "\n</new_message_from_interviewer>\n\n"
              "Reply as the owner, in plain text, briefly, the way a busy person answers. Answer only what was asked. "
              "Use what the persona says. If the persona doesn't cover something, give a short answer that fits it, "
              "or say you're not sure. Don't volunteer roadmap items nobody asked about, and never mention the persona.")
    env = {k: v for k, v in os.environ.items() if k not in STRIP_ENV}
    cmd = ["claude", "-p", "--max-turns", "1"] + (["--model", cfg["model"]] if cfg.get("model") else [])
    p = subprocess.run(cmd, input=prompt, capture_output=True, text=True, env=env, cwd=os.path.dirname(cfg["log"]))
    answer = p.stdout.strip() or "(the owner didn't answer: " + p.stderr.strip()[:200] + ")"
    with open(cfg["log"], "a") as f:
        f.write("## Interviewer\n\n" + q + "\n\n## Owner\n\n" + answer + "\n\n")
    print(answer)


if __name__ == "__main__":
    main()
