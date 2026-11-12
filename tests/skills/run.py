#!/usr/bin/env python3
"""Skill evals: run each connascence skill headlessly on a realistic repo, then grade what it did.

Each case in tests/skills/cases/<id>/case.json describes:
  - the starting repo (a fixture, git history, futures, baseline, a pending change)
  - the prompt a user would give
  - checks: scripted pass/fail tests on the resulting repo (futures.json, decisions log, diff, gate)
  - rubric: judgement calls graded by a separate model, which sees the skill, the transcript summary and the diff

Interview cases also have owner.md: a product-owner persona. The agent reaches the owner only through an
`ask-owner "<questions>"` command, which answers in character via a separate model call. The agent never
sees the persona file, so vague questions get vague answers.

Needs: the `claude` CLI (logged in), dotnet, git, a built CLI (`dotnet build`).
Costs real API usage: each case is one agent session plus one judge call.

Usage:
  python3 tests/skills/run.py                       # every case once
  python3 tests/skills/run.py --case review-        # cases whose id contains the text
  python3 tests/skills/run.py --runs 3              # repeat to see how consistent a skill is
  python3 tests/skills/run.py --list
  options: --model M (agent), --judge-model M, --owner-model M, --out DIR, --jobs N, --no-judge
"""
import argparse
import concurrent.futures
import datetime
import glob
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import textwrap
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
FIXTURES = os.path.join(REPO, "tests", "fixtures")
SKILLS = os.path.join(REPO, "skills")

# Commits that make some files "hot", so the explore skill's git-history step has something to find.
HISTORY = [
    ("Tally.Billing/InvoiceService.cs", "Fix overdue reminders going to paid invoices"),
    ("Tally.Api/InvoicesController.cs", "Show overdue invoices in the outstanding list"),
    ("Tally.Billing/InvoiceService.cs", "Round GST half away from zero"),
    ("web/invoice-list.js", "Amber badge for sent invoices"),
    ("Tally.Api/InvoicesController.cs", "Accountants can see outstanding invoices"),
    ("Tally.Billing/InvoiceService.cs", "Resend reminder only for sent invoices"),
    ("Tally.Billing/PaymentGateway.cs", "Retry card charges three times"),
    ("Tally.Api/InvoicesController.cs", "Status label for overdue"),
]

# Environment variables that tie a nested claude process to the session running this script.
STRIP_ENV = ["CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
             "CLAUDE_CODE_TEE_SDK_STDOUT", "CLAUDE_CODE_DIAGNOSTICS_FILE", "CLAUDE_CODE_ENTRYPOINT"]


# ---------------------------------------------------------------- helpers

def sh(cmd, cwd, env=None, check=True, timeout=None, input_text=None):
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, env=env, timeout=timeout, input=input_text)
    if check and p.returncode != 0:
        raise RuntimeError(" ".join(cmd) + " failed (" + str(p.returncode) + "):\n" + p.stdout + p.stderr)
    return p


GIT_IDENTITY = {"GIT_AUTHOR_NAME": "Sam", "GIT_AUTHOR_EMAIL": "sam@example.com",
                "GIT_COMMITTER_NAME": "Sam", "GIT_COMMITTER_EMAIL": "sam@example.com"}


def git_env():
    return dict(os.environ, **GIT_IDENTITY)


def git(cwd, *args, check=True):
    return sh(["git"] + list(args), cwd, env=git_env(), check=check).stdout


def claude_env(extra_path):
    env = {k: v for k, v in os.environ.items() if k not in STRIP_ENV}
    env.update(GIT_IDENTITY)
    if extra_path:
        env["PATH"] = extra_path + os.pathsep + env.get("PATH", "")
    return env


def find_cli_dll():
    dlls = sorted(glob.glob(os.path.join(REPO, "src", "Connascence.Cli", "bin", "*", "net11.0", "connascence.dll")),
                  key=os.path.getmtime, reverse=True)
    if not dlls:
        sys.exit("Build the CLI first: dotnet build")
    return dlls[0]


def write(path, text, mode=0o644):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)
    os.chmod(path, mode)


def read(path, default=None):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return default


def load_json(path):
    text = read(path)
    if text is None:
        return None
    try:
        return json.loads(text)
    except ValueError:
        return None


def truncate(text, n):
    return text if len(text) <= n else text[:n] + "\n... [" + str(len(text) - n) + " more characters]"


# ---------------------------------------------------------------- workspace

class Workspace:
    def __init__(self, case, run_dir, cli_dll, owner_model):
        self.case = case
        self.dir = run_dir
        self.repo = os.path.join(run_dir, "repo")
        self.bin = os.path.join(run_dir, "bin")
        self.cli_dll = cli_dll
        self.owner_model = owner_model
        self.setup_sha = None

    def connascence(self, *args, check=False):
        return sh(["dotnet", self.cli_dll] + list(args), self.repo, env=git_env(), check=check)

    def build(self):
        c = self.case
        os.makedirs(self.repo)
        shutil.copytree(os.path.join(FIXTURES, c.get("fixture", "tally")), self.repo, dirs_exist_ok=True)
        for skill in c.get("skills", ["connascence-review", "connascence-futures-explore", "connascence-futures-interview"]):
            shutil.copytree(os.path.join(SKILLS, skill), os.path.join(self.repo, ".claude", "skills", skill))
        write(os.path.join(self.bin, "connascence"), "#!/bin/sh\nexec dotnet " + shlex.quote(self.cli_dll) + ' "$@"\n', 0o755)
        if c.get("owner"):
            self._owner_command()

        git(self.repo, "init", "-q", "-b", "main")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-qm", "Initial import")
        if c.get("history", True):
            for i, (path, message) in enumerate(HISTORY):
                full = os.path.join(self.repo, path)
                text = read(full)
                write(full, text.rstrip("\n") + "\n" if text.endswith("\n\n") else text + "\n")  # toggle a trailing blank line
                git(self.repo, "commit", "-qam", message)
            for path, _ in HISTORY:  # leave every file exactly as the fixture has it
                shutil.copy(os.path.join(FIXTURES, c.get("fixture", "tally"), path), os.path.join(self.repo, path))
            git(self.repo, "commit", "-qam", "Tidy whitespace", check=False)

        # Adopt the tool the way the README says: init --hook, then (optionally) futures and a baseline.
        init = ["init", self.repo] + (["--hook"] if c.get("hook", True) else [])
        self.connascence(*init, check=True)
        if c.get("futures") is not None:
            write(os.path.join(self.repo, ".connascence", "futures.json"), json.dumps(c["futures"], indent=2) + "\n")
        if c.get("agentsSnippet"):
            snippet = read(os.path.join(REPO, "docs", "AGENTS-snippet.md"))
            body = snippet.split("```markdown", 1)[1].rsplit("```", 1)[0].strip()
            write(os.path.join(self.repo, "CLAUDE.md"), "# Tally\n\nInvoicing app. See README.md.\n\n" + body + "\n")
        if c.get("baseline", True):
            self.connascence("baseline", self.repo, "--no-git", check=True)
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-qm", "Adopt connascence", "--no-verify")

        # The change the user is working on.
        for w in c.get("writes", []):
            write(os.path.join(self.repo, w["file"]), w["content"])
        for e in c.get("edits", []):
            full = os.path.join(self.repo, e["file"])
            text = read(full)
            if e["old"] not in text:
                raise RuntimeError("case edit not applied: " + e["old"])
            write(full, text.replace(e["old"], e["new"]))
        if c.get("stage", False):
            git(self.repo, "add", "-A")
        self.setup_sha = git(self.repo, "rev-parse", "HEAD").strip()
        self.setup_files = self.snapshot()
        write(os.path.join(self.dir, "setup.json"), json.dumps({"sha": self.setup_sha, "files": self.setup_files}))

    def load_setup(self):
        data = load_json(os.path.join(self.dir, "setup.json"))
        self.setup_sha, self.setup_files = data["sha"], data["files"]

    def _owner_command(self):
        persona = os.path.join(self.dir, "owner-persona.md")
        shutil.copy(os.path.join(self.case["_dir"], self.case["owner"]), persona)
        cfg = os.path.join(self.dir, "owner.json")
        write(cfg, json.dumps({"persona": persona, "log": os.path.join(self.dir, "owner-log.md"), "model": self.owner_model}))
        write(os.path.join(self.bin, "ask-owner"), "#!/bin/sh\nexec python3 " + shlex.quote(os.path.join(HERE, "ask_owner.py")) +
              " " + shlex.quote(cfg) + ' "$@"\n', 0o755)

    def snapshot(self):
        """Content of every file in the repo, so changes can be detected exactly, committed or not."""
        snap = {}
        for dirpath, dirnames, names in os.walk(self.repo):
            dirnames[:] = [d for d in dirnames if d not in (".git", "bin", "obj")]
            for n in names:
                full = os.path.join(dirpath, n)
                rel = os.path.relpath(full, self.repo).replace(os.sep, "/")
                if rel.startswith(".connascence/log/runs"):
                    continue
                try:
                    with open(full, "rb") as f:
                        snap[rel] = hashlib.sha1(f.read()).hexdigest()
                except OSError:
                    pass
        return snap


# ---------------------------------------------------------------- running the agent

def run_agent(ws, model, timeout):
    c = ws.case
    prompt = c["prompt"]
    if c.get("owner"):
        prompt += ("\n\n(The product owner can't join this session live. Reach them with the `ask-owner` shell command: "
                   "`ask-owner \"<your questions>\"` prints their reply. Send each round of questions as one call, "
                   "with the options you'd offer them.)")
    cmd = ["claude", "-p", prompt, "--output-format", "stream-json", "--verbose",
           "--allowedTools", "Bash,Read,Write,Edit,Glob,Grep,Skill,TodoWrite",
           "--disallowedTools", "AskUserQuestion,WebFetch,WebSearch,Agent,Task",
           "--max-turns", str(c.get("maxTurns", 80))]
    if model:
        cmd += ["--model", model]
    started = time.time()
    try:
        p = subprocess.run(cmd, cwd=ws.repo, capture_output=True, text=True, env=claude_env(ws.bin), timeout=timeout)
        out, err, rc = p.stdout, p.stderr, p.returncode
    except subprocess.TimeoutExpired as ex:
        out = ex.stdout.decode() if isinstance(ex.stdout, bytes) else (ex.stdout or "")
        err, rc = "timed out after " + str(timeout) + "s", -1
    write(os.path.join(ws.dir, "transcript.jsonl"), out)
    if err.strip():
        write(os.path.join(ws.dir, "agent-stderr.txt"), err)
    t = parse_transcript(out)
    t["exit"] = rc
    t["seconds"] = round(time.time() - started)
    return t


def parse_transcript(stream):
    t = {"commands": [], "skills": [], "edits": [], "tools": [], "final": "", "cost": None, "turns": None, "texts": []}
    for line in stream.splitlines():
        try:
            ev = json.loads(line)
        except ValueError:
            continue
        if ev.get("type") == "assistant":
            for block in ev.get("message", {}).get("content", []):
                if block.get("type") == "text" and block.get("text", "").strip():
                    t["texts"].append(block["text"].strip())
                if block.get("type") != "tool_use":
                    continue
                name, inp = block.get("name"), block.get("input", {})
                t["tools"].append(name)
                if name == "Bash":
                    t["commands"].append(inp.get("command", ""))
                elif name == "Skill":
                    t["skills"].append(inp.get("skill") or inp.get("command") or json.dumps(inp))
                elif name in ("Edit", "Write", "MultiEdit", "NotebookEdit"):
                    t["edits"].append(inp.get("file_path", ""))
        elif ev.get("type") == "result":
            t["final"] = ev.get("result") or ""
            t["cost"] = ev.get("total_cost_usd")
            t["turns"] = ev.get("num_turns")
    return t


# ---------------------------------------------------------------- checks

class Grader:
    def __init__(self, ws, transcript):
        self.ws = ws
        self.t = transcript
        self.repo = ws.repo
        self.futures = load_json(os.path.join(self.repo, ".connascence", "futures.json"))
        self.decisions = [json.loads(l) for l in (read(os.path.join(self.repo, ".connascence", "log", "decisions.jsonl"), "") or "").splitlines() if l.strip()]
        self._report = None

    def report(self):
        if self._report is None:
            p = self.ws.connascence("analyze", self.repo, "--no-git", "--format", "json", "--all", "--top", "10000")
            self._report = json.loads(p.stdout) if p.returncode == 0 else {"findings": [], "error": p.stderr}
        return self._report

    def future_list(self):
        return (self.futures or {}).get("futures") or []

    @staticmethod
    def text_of(f):
        return " ".join([f.get("id") or "", f.get("title") or "", f.get("description") or ""] + (f.get("keywords") or []) + (f.get("areas") or []))

    def changed_files(self):
        now = self.ws.snapshot()
        before = self.ws.setup_files
        return sorted(k for k in set(now) | set(before) if now.get(k) != before.get(k))

    def run(self, check):
        kind = check["type"]
        fn = getattr(self, "c_" + kind, None)
        if fn is None:
            return False, "unknown check type " + kind
        try:
            return fn(check)
        except Exception as ex:  # a broken artefact is a failed check, not a crashed run
            return False, type(ex).__name__ + ": " + str(ex)

    # --- futures.json

    def c_futures_valid(self, c):
        if self.futures is None:
            return False, ".connascence/futures.json is missing or not valid JSON"
        fs = self.future_list()
        problems = []
        lo, hi = c.get("min", 5), c.get("max", 15)
        if not lo <= len(fs) <= hi:
            problems.append(str(len(fs)) + " futures, wanted " + str(lo) + "-" + str(hi))
        ids = [f.get("id") for f in fs]
        if len(set(ids)) != len(ids):
            problems.append("duplicate ids: " + ", ".join(ids))
        for f in fs:
            if (f.get("likelihood") or "").lower() not in ("high", "medium", "low"):
                problems.append(str(f.get("id")) + " has likelihood " + repr(f.get("likelihood")))
            if not (f.get("areas") or f.get("keywords")) and not re.search(r"(?i)new code", f.get("description") or ""):
                problems.append(str(f.get("id")) + " has no areas or keywords")
            if (f.get("source") or "") == "example":
                problems.append(str(f.get("id")) + " is still the init template's example")
        return not problems, "; ".join(problems) or str(len(fs)) + " futures, ids " + ", ".join(ids)

    def c_futures_sources(self, c):
        sources = [(f.get("source") or "") for f in self.future_list()]
        bad = [s for s in sources if s not in c["allowed"]]
        missing = [s for s in c.get("require", []) if s not in sources]
        ok = not bad and not missing
        return ok, "sources: " + ", ".join(sources) + ("; not allowed: " + ", ".join(bad) if bad else "") + ("; missing: " + ", ".join(missing) if missing else "")

    def c_futures_mention(self, c):
        hits = [f for f in self.future_list() if re.search(c["pattern"], self.text_of(f))]
        if c.get("likelihood"):
            good = [f for f in hits if (f.get("likelihood") or "").lower() in c["likelihood"]]
            return bool(good), (c.get("label") or c["pattern"]) + ": " + (", ".join(f["id"] + " " + f.get("title", "") + " (" + str(f.get("likelihood")) + ")" for f in hits) or "no future mentions it")
        return bool(hits), (c.get("label") or c["pattern"]) + ": " + (", ".join(f["id"] + " " + f.get("title", "") for f in hits) or "no future mentions it")

    def c_futures_absent(self, c):
        hits = [f for f in self.future_list() if re.search(c["pattern"], self.text_of(f))
                and not (c.get("allowLow") and (f.get("likelihood") or "").lower() == "low")]
        return not hits, (c.get("label") or c["pattern"]) + ": " + (", ".join(f["id"] + " " + f.get("title", "") + " (" + str(f.get("likelihood")) + ")" for f in hits) or "none")

    def c_stable_mention(self, c):
        stable = (self.futures or {}).get("stable") or []
        hits = [s for s in stable if re.search(c["pattern"], " ".join([s.get("reason") or ""] + (s.get("areas") or []) + (s.get("keywords") or [])))]
        return bool(hits), "stable areas: " + (json.dumps(stable)[:400] if stable else "none")

    def c_futures_touch_code(self, c):
        fs = self.future_list()
        if not fs:
            return False, "no futures"
        touched = {fid for f in self.report()["findings"] for fid in (f.get("futures") or [])}
        new_code = {f["id"] for f in fs if re.search(r"(?i)new code|all new|no existing code", f.get("description") or "")}
        share = len([f for f in fs if f["id"] in touched or f["id"] in new_code]) / len(fs)
        untouched = [f["id"] for f in fs if f["id"] not in touched and f["id"] not in new_code]
        return share >= c.get("minShare", 0.6), "{:.0%} of futures touch a finding or say they are new code; untouched: {}".format(share, ", ".join(untouched) or "none")

    def c_futures_preserved(self, c):
        by_id = {f.get("id"): f for f in self.future_list()}
        problems = []
        for want in c["futures"]:
            got = by_id.get(want["id"])
            if got is None:
                problems.append(want["id"] + " is gone")
                continue
            for k, v in want.items():
                if got.get(k) != v:
                    problems.append(want["id"] + "." + k + " changed to " + repr(got.get(k)))
        return not problems, "; ".join(problems) or "kept " + ", ".join(w["id"] for w in c["futures"])

    def c_future_likelihood(self, c):
        by_id = {f.get("id"): f for f in self.future_list()}
        f = by_id.get(c["id"])
        if f is None:
            return c.get("removedOk", False), c["id"] + " removed"
        lk = (f.get("likelihood") or "").lower()
        return lk in c["allowed"], c["id"] + " likelihood " + lk

    # --- decisions and findings

    def _decisions(self, c):
        out = []
        for d in self.decisions:
            if c.get("choice") and (d.get("decision") or d.get("choice")) != c["choice"]:
                continue
            if c.get("rule") and d.get("ruleId") != c["rule"]:
                continue
            if c.get("titlePattern") and not re.search(c["titlePattern"], d.get("title") or ""):
                continue
            out.append(d)
        return out

    def c_decision(self, c):
        ds = self._decisions(c)
        all_ = "; ".join((d.get("decision") or d.get("choice") or "") + " " + (d.get("ruleId") or "") + " " + (d.get("title") or "") + ": " + (d.get("reason") or "") for d in self.decisions) or "no decisions logged"
        if not ds:
            return False, "no matching decision. Logged: " + all_
        if c.get("reasonPattern"):
            ds = [d for d in ds if re.search(c["reasonPattern"], d.get("reason") or "")]
            if not ds:
                return False, "decision found but the reason doesn't match " + c["reasonPattern"] + ". Logged: " + all_
        if c.get("future"):
            ds = [d for d in ds if (d.get("future") or "") == c["future"]]
            if not ds:
                return False, "decision found but not linked to " + c["future"] + ". Logged: " + all_
        return True, all_

    def c_no_decision(self, c):
        ds = self._decisions(c)
        return not ds, "; ".join((d.get("decision") or d.get("choice") or "") + " " + (d.get("title") or "") + ": " + (d.get("reason") or "") for d in ds) or "none"

    def c_finding(self, c):
        fs = [f for f in self.report()["findings"] if f["ruleId"] == c["rule"] and (not c.get("groupKey") or f.get("groupKey") == c["groupKey"])
              and (not c.get("titlePattern") or re.search(c["titlePattern"], f["title"]))]
        open_ = [f for f in fs if not f.get("accepted")]
        desc = ", ".join(f["title"] + " degree " + str(f["degree"]) + (" (accepted)" if f.get("accepted") else "") for f in fs) or "none"
        if "maxDegree" in c:
            return all(f["degree"] <= c["maxDegree"] for f in fs), desc
        if c.get("absent"):
            return not open_, desc
        return bool(fs), desc

    def c_gate_passes(self, c):
        git(self.repo, "add", "-A")
        p = self.ws.connascence("check", self.repo, "--since", self.ws.setup_sha, "--no-git")
        return p.returncode == 0, "exit " + str(p.returncode) + ("\n" + truncate(p.stdout, 1500) if p.returncode else "")

    def c_untouched(self, c):
        changed = self.changed_files()
        bad = [f for f in changed if any(re.search(p, f) for p in c["patterns"])]
        return not bad, "changed: " + (", ".join(changed) or "nothing")

    def c_committed(self, c):
        head = git(self.repo, "rev-parse", "HEAD").strip()
        if head == self.ws.setup_sha:
            return False, "no new commit"
        log = git(self.repo, "log", "--oneline", self.ws.setup_sha + "..HEAD")
        hooks_skipped = [x for x in self.t["commands"] if "--no-verify" in x or "-n " in x and "git commit" in x]
        if hooks_skipped and not c.get("allowNoVerify"):
            return False, "committed with the hook skipped: " + hooks_skipped[0]
        return True, log.strip()

    def c_files_committed(self, c):
        dirty = git(self.repo, "status", "--porcelain", "--", *c["paths"], check=False).strip()
        return not dirty, "uncommitted: " + dirty.replace("\n", "; ") if dirty else "all committed"

    def c_not_committed(self, c):
        head = git(self.repo, "rev-parse", "HEAD").strip()
        return head == self.ws.setup_sha, "HEAD unchanged" if head == self.ws.setup_sha else "new commits: " + git(self.repo, "log", "--oneline", self.ws.setup_sha + "..HEAD").strip()

    def c_derived_parents(self, c):
        derived = [f for f in self.future_list() if (f.get("source") or "") == "derived"]
        ids = {f.get("id") for f in self.future_list() if (f.get("source") or "") != "derived"}
        orphans = [f["id"] for f in derived if not any(re.search(r"\b" + re.escape(i) + r"\b", (f.get("description") or "") + " " + (f.get("title") or "")) for i in ids if i)]
        return bool(derived) and not orphans, str(len(derived)) + " derived; without a parent id: " + (", ".join(orphans) or "none")

    # --- transcript

    def c_command_ran(self, c):
        hits = [x for x in self.t["commands"] if re.search(c["pattern"], x)]
        return bool(hits), (hits[0][:200] if hits else "no command matched " + c["pattern"])

    def c_skill_used(self, c):
        hits = [s for s in self.t["skills"] if c["skill"] in s]
        read_it = [x for x in self.t["commands"] if c["skill"] in x]
        ok = bool(hits) or (c.get("orRead") and bool(read_it))
        return ok, "skills invoked: " + (", ".join(self.t["skills"]) or "none")

    def c_final_mentions(self, c):
        text = self.t["final"] + "\n" + "\n".join(self.t["texts"][-3:])
        return bool(re.search(c["pattern"], text)), (c.get("label") or c["pattern"])

    def c_file_exists(self, c):
        text = read(os.path.join(self.repo, c["path"]))
        if text is None:
            return False, c["path"] + " missing"
        if c.get("pattern") and not re.search(c["pattern"], text):
            return False, c["path"] + " lacks " + c["pattern"]
        return True, c["path"] + " (" + str(len(text)) + " chars)"

    def c_owner_calls(self, c):
        log = read(os.path.join(self.ws.dir, "owner-log.md"), "")
        n = log.count("## Interviewer")
        return c.get("min", 1) <= n <= c.get("max", 99), str(n) + " calls to ask-owner"

    def c_any_of(self, c):
        notes = []
        for sub in c["checks"]:
            ok, note = self.run(sub)
            notes.append(("ok " if ok else "no ") + sub["type"] + ": " + note)
            if ok:
                return True, " | ".join(notes)
        return False, " | ".join(notes)


# ---------------------------------------------------------------- judge

JUDGE_PROMPT = """You are grading how well an AI coding agent followed a skill. Be strict and fair. Judge only from the evidence below.

<skill name="{skill}">
{skill_text}
</skill>

<case>
{description}
</case>

<user_prompt>
{prompt}
</user_prompt>

<agent_final_message>
{final}
</agent_final_message>

<agent_messages_along_the_way>
{texts}
</agent_messages_along_the_way>

<shell_commands_the_agent_ran>
{commands}
</shell_commands_the_agent_ran>

<files_the_agent_edited>
{edits}
</files_the_agent_edited>

<diff_since_the_case_started>
{diff}
</diff_since_the_case_started>

<futures_json_after>
{futures}
</futures_json_after>

<decisions_logged>
{decisions}
</decisions_logged>

<owner_interview_log>
{owner_log}
</owner_interview_log>

Grade each criterion. A criterion passes only if the evidence clearly supports it.

<criteria>
{criteria}
</criteria>

Reply with JSON only, no prose before or after:
{{"criteria": [{{"id": 1, "pass": true, "evidence": "one or two sentences"}}, ...], "summary": "one sentence on the biggest weakness"}}"""


def judge(ws, transcript, grader, model):
    c = ws.case
    skill_text = read(os.path.join(SKILLS, c["skill"], "SKILL.md"), "")
    diff = git(ws.repo, "diff", ws.setup_sha, "--", ".", ":(exclude).connascence/log/runs.jsonl", check=False)
    untracked = git(ws.repo, "ls-files", "--others", "--exclude-standard", check=False).split()
    for u in untracked[:10]:
        diff += "\n--- new file " + u + "\n" + truncate(read(os.path.join(ws.repo, u), ""), 3000)
    prompt = JUDGE_PROMPT.format(
        skill=c["skill"], skill_text=skill_text, description=c.get("description", ""), prompt=c["prompt"],
        final=truncate(transcript["final"] or "(none)", 6000),
        texts=truncate("\n---\n".join(transcript["texts"][:-1]), 8000) or "(none)",
        commands=truncate("\n".join("$ " + x for x in transcript["commands"]), 8000) or "(none)",
        edits="\n".join(sorted(set(transcript["edits"]))) or "(none)",
        diff=truncate(diff, 20000) or "(no changes)",
        futures=truncate(json.dumps(grader.futures, indent=1), 8000) if grader.futures else "(none)",
        decisions="\n".join(json.dumps(d) for d in grader.decisions) or "(none)",
        owner_log=truncate(read(os.path.join(ws.dir, "owner-log.md"), "(not an interview case)"), 8000),
        criteria="\n".join(str(i + 1) + ". " + r for i, r in enumerate(c.get("rubric", []))))
    write(os.path.join(ws.dir, "judge-prompt.md"), prompt)
    cmd = ["claude", "-p", "--max-turns", "1", "--output-format", "json"] + (["--model", model] if model else [])
    with tempfile.TemporaryDirectory() as empty:
        p = subprocess.run(cmd, input=prompt, cwd=empty, capture_output=True, text=True, env=claude_env(""), timeout=600)
    try:
        result = json.loads(p.stdout)
        text = result.get("result", "")
        cost = result.get("total_cost_usd")
    except ValueError:
        text, cost = p.stdout, None
    m = re.search(r"\{.*\}", text, re.S)
    try:
        verdict = json.loads(m.group(0)) if m else None
    except ValueError:
        verdict = None
    if not verdict:
        return {"error": "judge reply was not JSON", "raw": text[:2000], "cost": cost}
    verdict["cost"] = cost
    return verdict


# ---------------------------------------------------------------- one case

def run_case(case, run_no, args, cli_dll, out_root):
    label = case["id"] + ("#" + str(run_no) if args.runs > 1 else "")
    run_dir = os.path.join(out_root, label.replace("#", "-"))
    ws = Workspace(case, run_dir, cli_dll, args.owner_model)
    result = {"case": case["id"], "run": run_no, "skill": case["skill"], "dir": run_dir}
    try:
        ws.build()
        t = run_agent(ws, args.model, args.timeout)
        grade(ws, t, result, args)
    except Exception as ex:
        result["error"] = type(ex).__name__ + ": " + str(ex)
    with open(os.path.join(run_dir, "result.json"), "w") as f:
        json.dump(result, f, indent=2)
    return result


def regrade_case(case, run_dir, args, cli_dll):
    """Re-apply checks and the judge to a finished run, without running the agent again."""
    ws = Workspace(case, run_dir, cli_dll, args.owner_model)
    ws.load_setup()
    old = load_json(os.path.join(run_dir, "result.json")) or {}
    result = {"case": case["id"], "run": old.get("run", 1), "skill": case["skill"], "dir": run_dir}
    try:
        t = parse_transcript(read(os.path.join(run_dir, "transcript.jsonl"), ""))
        t["exit"], t["seconds"] = old.get("agentExit"), old.get("seconds")
        grade(ws, t, result, args)
    except Exception as ex:
        result["error"] = type(ex).__name__ + ": " + str(ex)
    with open(os.path.join(run_dir, "result.json"), "w") as f:
        json.dump(result, f, indent=2)
    return result


def grade(ws, t, result, args):
    case = ws.case
    result.update({"agentExit": t["exit"], "seconds": t["seconds"], "agentCost": t["cost"], "turns": t["turns"],
                   "skillsInvoked": t["skills"]})
    g = Grader(ws, t)
    checks = []
    for chk in case.get("checks", []):
        ok, note = g.run(chk)
        checks.append({"check": chk.get("label") or chk["type"], "pass": ok, "note": note})
    result["checks"] = checks
    if not args.no_judge and case.get("rubric"):
        result["judge"] = judge(ws, t, g, args.judge_model)


def summarise(results, out_root):
    lines = ["# Skill eval results", "", "Run " + datetime.datetime.now().strftime("%Y-%m-%d %H:%M") + ". Artefacts per case in " + out_root, ""]
    lines += ["| Case | Checks | Rubric | Cost | Time |", "|---|---|---|---|---|"]
    total_cost = 0.0
    for r in results:
        checks = r.get("checks", [])
        passed = sum(1 for c in checks if c["pass"])
        crit = (r.get("judge") or {}).get("criteria") or []
        rub = sum(1 for c in crit if c.get("pass"))
        cost = (r.get("agentCost") or 0) + ((r.get("judge") or {}).get("cost") or 0)
        total_cost += cost
        name = r["case"] + ("#" + str(r["run"]) if r.get("run", 1) > 1 else "")
        lines.append("| {} | {} | {} | ${:.2f} | {}s |".format(
            name, "error" if r.get("error") else "{}/{}".format(passed, len(checks)),
            "{}/{}".format(rub, len(crit)) if crit else ("judge error" if r.get("judge") else "-"), cost, r.get("seconds", "-")))
    lines += ["", "Total cost ${:.2f}".format(total_cost), ""]
    for r in results:
        name = r["case"] + ("#" + str(r["run"]) if r.get("run", 1) > 1 else "")
        lines += ["## " + name, ""]
        if r.get("error"):
            lines += ["Error: " + r["error"], ""]
        if r.get("skillsInvoked") is not None:
            lines += ["Skills invoked: " + (", ".join(r["skillsInvoked"]) or "none"), ""]
        for c in r.get("checks", []):
            lines.append("- " + ("PASS" if c["pass"] else "FAIL") + " " + c["check"] + ": " + c["note"].replace("\n", " ")[:400])
        j = r.get("judge") or {}
        if j.get("criteria"):
            lines.append("")
            case = next((c for c in CASES if c["id"] == r["case"]), {})
            rubric = case.get("rubric", [])
            for c in j["criteria"]:
                i = c.get("id", 0)
                text = rubric[i - 1] if 0 < i <= len(rubric) else "criterion " + str(i)
                lines.append("- " + ("PASS" if c.get("pass") else "FAIL") + " (judge) " + text + " - " + (c.get("evidence") or ""))
            if j.get("summary"):
                lines += ["", "Judge: " + j["summary"]]
        elif j.get("error"):
            lines.append("- judge error: " + j["error"])
        lines.append("")
    return "\n".join(lines)


CASES = []


def load_cases():
    for path in sorted(glob.glob(os.path.join(HERE, "cases", "*", "case.json"))):
        with open(path) as f:
            c = json.load(f)
        c["id"] = os.path.basename(os.path.dirname(path))
        c["_dir"] = os.path.dirname(path)
        CASES.append(c)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--case", default="", help="run cases whose id contains this text")
    ap.add_argument("--runs", type=int, default=1)
    ap.add_argument("--jobs", type=int, default=3)
    ap.add_argument("--model")
    ap.add_argument("--judge-model")
    ap.add_argument("--owner-model", default="haiku")
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--out")
    ap.add_argument("--no-judge", action="store_true")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--dry-run", action="store_true", help="build each case's repo and run the hook, without calling claude")
    ap.add_argument("--regrade", metavar="DIR", help="re-run checks and the judge on an earlier --out folder, without running agents")
    args = ap.parse_args()

    load_cases()
    cases = [c for c in CASES if args.case in c["id"]]
    if args.list or not cases:
        for c in CASES:
            print("{:<40} {}".format(c["id"], c.get("description", "")[:90]))
        return 0 if args.list else 2
    if args.dry_run:
        cli_dll = find_cli_dll()
        out_root = args.out or tempfile.mkdtemp(prefix="connascence-skill-dry-")
        for c in cases:
            ws = Workspace(c, os.path.join(out_root, c["id"]), cli_dll, None)
            ws.build()
            p = ws.connascence("check", ws.repo, "--staged", "--no-git")
            lines = [l for l in p.stdout.splitlines() if re.match(r"^\s*(\d+\.|[0-9a-f]{10}|.*(grew|new)\b)", l)]
            print("{:<38} hook exit {}  {}".format(c["id"], p.returncode, " | ".join(l.strip() for l in lines[:6])[:300]))
        print("Repos in " + out_root)
        return 0
    if not shutil.which("claude"):
        sys.exit("The claude CLI is needed for skill evals.")
    cli_dll = find_cli_dll()
    out_root = args.regrade or args.out or os.path.join(tempfile.gettempdir(), "connascence-skill-evals", datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
    os.makedirs(out_root, exist_ok=True)
    print(("Re-grading " if args.regrade else "Writing to ") + out_root)

    results = []
    if args.regrade:
        out_root = args.regrade
        by_id = {c["id"]: c for c in cases}
        jobs = []
        for d in sorted(glob.glob(os.path.join(out_root, "*", "setup.json"))):
            run_dir = os.path.dirname(d)
            cid = re.sub(r"-\d+$", "", os.path.basename(run_dir)) if os.path.basename(run_dir) not in by_id else os.path.basename(run_dir)
            if cid in by_id:
                jobs.append((by_id[cid], run_dir))
        submit = lambda pool, c, x: pool.submit(regrade_case, c, x, args, cli_dll)
    else:
        jobs = [(c, n) for c in cases for n in range(1, args.runs + 1)]
        submit = lambda pool, c, n: pool.submit(run_case, c, n, args, cli_dll, out_root)
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        futs = {submit(pool, c, n): (c, n) for c, n in jobs}
        for fut in concurrent.futures.as_completed(futs):
            r = fut.result()
            checks = r.get("checks", [])
            crit = (r.get("judge") or {}).get("criteria") or []
            print("{:<40} checks {}/{}  rubric {}/{}{}".format(
                r["case"] + ("#" + str(r["run"]) if args.runs > 1 else ""),
                sum(1 for c in checks if c["pass"]), len(checks), sum(1 for c in crit if c.get("pass")), len(crit),
                "  ERROR " + r["error"] if r.get("error") else ""))
            results.append(r)
    results.sort(key=lambda r: (r["case"], r["run"]))
    summary = summarise(results, out_root)
    write(os.path.join(out_root, "summary.md"), summary)
    with open(os.path.join(out_root, "results.json"), "w") as f:
        json.dump(results, f, indent=2)
    print("\n" + summary)
    failed = [r for r in results if r.get("error") or any(not c["pass"] for c in r.get("checks", []))]
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
