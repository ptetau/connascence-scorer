#!/usr/bin/env python3
"""Scenario tests for the connascence CLI.

Three suites, each a folder of scenarios:

  detection/  Does each detector find the coupling it should, and stay quiet where it shouldn't?
  changes/    When a realistic change lands, did the tool point at the places that had to change together?
  gate/       Does `connascence check` block and pass the right commits?

Each scenario is a folder with a scenario.json. An expectation can carry "gap": "<why>" to mark a known
weakness. A gap that fails is reported as GAP and doesn't fail the run. A gap that passes is reported as
FIXED and does fail the run, so the marker gets removed and the improvement is locked in.

Usage:
  python3 tests/scenarios/run.py [--suite detection|changes|gate] [--filter text] [--cli "<command>"] [-v]

The CLI defaults to the built dll (src/Connascence.Cli/bin/*/net8.0/connascence.dll), then `connascence` on PATH.
"""
import argparse
import difflib
import glob
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
FIXTURES = os.path.join(REPO, "tests", "fixtures")


# ---------------------------------------------------------------- plumbing

def find_cli(explicit):
    if explicit:
        return shlex.split(explicit)
    if os.environ.get("CONNASCENCE_CLI"):
        return shlex.split(os.environ["CONNASCENCE_CLI"])
    dlls = sorted(glob.glob(os.path.join(REPO, "src", "Connascence.Cli", "bin", "*", "net11.0", "connascence.dll")),
                  key=os.path.getmtime, reverse=True)
    if dlls:
        return ["dotnet", dlls[0]]
    if shutil.which("connascence"):
        return ["connascence"]
    sys.exit("Can't find the connascence CLI. Run `dotnet build` first, or pass --cli.")


class Ctx:
    def __init__(self, cli, verbose):
        self.cli = cli
        self.verbose = verbose

    def run(self, args, cwd, check=False):
        env = dict(os.environ, CONNASCENCE_ACTOR="scenario", GIT_AUTHOR_NAME="scenario", GIT_AUTHOR_EMAIL="s@example.com",
                   GIT_COMMITTER_NAME="scenario", GIT_COMMITTER_EMAIL="s@example.com")
        p = subprocess.run(self.cli + args, cwd=cwd, capture_output=True, text=True, env=env)
        if check and p.returncode != 0:
            raise RuntimeError("connascence " + " ".join(args) + " failed (" + str(p.returncode) + "):\n" + p.stdout + p.stderr)
        return p

    def analyze(self, root, extra=()):
        p = self.run(["analyze", root, "--no-git", "--format", "json", "--all", "--top", "10000"] + list(extra), cwd=root, check=True)
        return json.loads(p.stdout)


def git(cwd, *args):
    env = dict(os.environ, GIT_AUTHOR_NAME="scenario", GIT_AUTHOR_EMAIL="s@example.com",
               GIT_COMMITTER_NAME="scenario", GIT_COMMITTER_EMAIL="s@example.com")
    p = subprocess.run(["git"] + list(args), cwd=cwd, capture_output=True, text=True, env=env)
    if p.returncode != 0:
        raise RuntimeError("git " + " ".join(args) + ": " + p.stderr)
    return p.stdout


def copy_tree(src, dst):
    shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns("bin", "obj", ".connascence"))


def source_root(scenario_dir, spec, work):
    """Materialise the code a scenario runs against into `work` and return it."""
    if spec.get("fixture"):
        copy_tree(os.path.join(FIXTURES, spec["fixture"]), work)
    code = os.path.join(scenario_dir, "code")
    if os.path.isdir(code):
        copy_tree(code, work)
    if spec.get("futures") is not None:
        os.makedirs(os.path.join(work, ".connascence"), exist_ok=True)
        with open(os.path.join(work, ".connascence", "futures.json"), "w") as f:
            json.dump(spec["futures"], f, indent=2)
    if spec.get("config") is not None:
        with open(os.path.join(work, ".connascence.json"), "w") as f:
            json.dump(spec["config"], f, indent=2)
    return work


# ---------------------------------------------------------------- matching findings

def files_of(f):
    return sorted({l["file"] for l in f["locations"]})


def matches(f, m):
    """True if finding f satisfies every key in matcher m."""
    if "rule" in m and f["ruleId"] != m["rule"]:
        return False
    if "groupKey" in m and f.get("groupKey") != m["groupKey"]:
        return False
    if "groupKeyStartsWith" in m and not (f.get("groupKey") or "").startswith(m["groupKeyStartsWith"]):
        return False
    if "titleContains" in m and m["titleContains"].lower() not in f["title"].lower():
        return False
    if "files" in m and not set(m["files"]).issubset(files_of(f)):
        return False
    if "anyFile" in m and not set(m["anyFile"]) & set(files_of(f)):
        return False
    if "members" in m and not set(m["members"]).issubset({l.get("member") for l in f["locations"]}):
        return False
    if "lines" in m and not set(m["lines"]).issubset({l["file"] + ":" + str(l["line"]) for l in f["locations"]}):
        return False
    if "degree" in m and f["degree"] != m["degree"]:
        return False
    if "minDegree" in m and f["degree"] < m["minDegree"]:
        return False
    if "distance" in m and f["distance"] != m["distance"]:
        return False
    if "accepted" in m and bool(f.get("accepted")) != m["accepted"]:
        return False
    if "minCost" in m and f["cost"] < m["minCost"]:
        return False
    if "future" in m and m["future"] not in (f.get("futures") or []):
        return False
    if "stable" in m and bool(f.get("stableReason")) != m["stable"]:
        return False
    return True


def describe(m):
    return ", ".join(k + "=" + (json.dumps(v) if not isinstance(v, str) else v) for k, v in m.items() if k not in ("gap", "why"))


def brief(f):
    return f["ruleId"] + " " + f["title"] + " (degree " + str(f["degree"]) + ", " + f["distance"] + ", cost " + str(f["cost"]) + ") " + ", ".join(files_of(f))


# ---------------------------------------------------------------- results

class Results:
    def __init__(self):
        self.rows = []

    def add(self, suite, scenario, check, ok, detail="", gap=None):
        if gap:
            status = "FIXED" if ok else "GAP"
        else:
            status = "pass" if ok else "FAIL"
        self.rows.append((suite, scenario, check, status, detail, gap))

    def failed(self):
        return [r for r in self.rows if r[3] in ("FAIL", "FIXED")]

    def print(self, verbose):
        current = None
        for suite, scenario, check, status, detail, gap in self.rows:
            if (suite, scenario) != current:
                current = (suite, scenario)
                print("\n" + suite + "/" + scenario)
            if status == "pass" and not verbose:
                continue
            print("  {:<5} {}".format(status, check))
            if status != "pass" and detail:
                for line in detail.splitlines():
                    print("        " + line)
            if gap and status in ("GAP", "FIXED"):
                print("        known gap: " + gap)
                if status == "FIXED":
                    print("        This now passes. Remove the \"gap\" marker so it stays fixed.")
        counts = {}
        for r in self.rows:
            counts[r[3]] = counts.get(r[3], 0) + 1
        print("\n" + "  ".join(k + " " + str(v) for k, v in sorted(counts.items())))


# ---------------------------------------------------------------- detection suite

def run_detection(ctx, scenario_dir, spec, res, name):
    with tempfile.TemporaryDirectory() as tmp:
        root = source_root(scenario_dir, spec, tmp)
        report = ctx.analyze(root, ["--mode", spec["mode"]] if spec.get("mode") else [])
        findings = report["findings"]
        if report.get("warnings"):
            res.add("detection", name, "no analysis warnings", False, "\n".join(report["warnings"]))
        for e in spec.get("expect", []):
            hits = [f for f in findings if matches(f, e)]
            same_rule = [brief(f) for f in findings if f["ruleId"] == e.get("rule")]
            detail = "" if hits else "Nothing matched. Findings with that rule: " + ("\n" + "\n".join(same_rule) if same_rule else "none")
            res.add("detection", name, "finds " + describe(e) + (" - " + e["why"] if e.get("why") else ""), bool(hits), detail, e.get("gap"))
        for e in spec.get("forbid", []):
            hits = [f for f in findings if matches(f, e)]
            res.add("detection", name, "does not report " + describe(e) + (" - " + e["why"] if e.get("why") else ""),
                    not hits, "\n".join(brief(f) for f in hits), e.get("gap"))
        if "maxFindings" in spec:
            open_ = [f for f in findings if not f.get("accepted")]
            res.add("detection", name, "at most " + str(spec["maxFindings"]) + " findings", len(open_) <= spec["maxFindings"],
                    "\n".join(brief(f) for f in open_))


# ---------------------------------------------------------------- changes suite

def read_lines(path):
    with open(path, encoding="utf-8") as f:
        return f.read().splitlines()


def edits_between(before_root, after_root):
    """Map of file -> list of edits in before-file line numbers.

    An edit is (start, end): lines start..end (1-based, inclusive) were replaced or deleted.
    A pure insertion after line n is recorded as (n + 0.5, n + 0.5).
    New files appear with an empty list under key "+path"."""
    edits = {}
    after_files = set()
    for dirpath, _, names in os.walk(after_root):
        if ".connascence" in dirpath:
            continue
        for n in names:
            after_files.add(os.path.relpath(os.path.join(dirpath, n), after_root).replace(os.sep, "/"))
    for rel in sorted(after_files):
        b = os.path.join(before_root, rel)
        if not os.path.exists(b):
            edits["+" + rel] = []
            continue
        sm = difflib.SequenceMatcher(a=read_lines(b), b=read_lines(os.path.join(after_root, rel)), autojunk=False)
        hunks = []
        for tag, i1, i2, _, _ in sm.get_opcodes():
            if tag in ("replace", "delete"):
                hunks.append((i1 + 1, i2))
            elif tag == "insert":
                hunks.append((i1 + 0.5, i1 + 0.5))
        if hunks:
            edits[rel] = hunks
    return edits


def location_hit(loc, edits):
    start = loc["line"]
    end = max(loc.get("endLine") or 0, start)
    for a, b in edits.get(loc["file"], []):
        if a == b and a != int(a):            # insertion point
            if start < a < end:
                return True
        elif a <= end and b >= start:
            return True
    return False


def predicted(f, edits):
    """A finding predicts a change if the change edits at least two of its places (or its only place)."""
    hit = {(l["file"], l["line"]) for l in f["locations"] if location_hit(l, edits)}
    need = min(2, len({(l["file"], l["line"]) for l in f["locations"]}))
    return len(hit) >= need, len(hit)


def run_change(ctx, scenario_dir, spec, res, name):
    with tempfile.TemporaryDirectory() as tmp:
        before = os.path.join(tmp, "before")
        after = os.path.join(tmp, "after")
        source_root(scenario_dir, spec, before)
        copy_tree(before, after)
        if os.path.isdir(os.path.join(scenario_dir, "after")):
            copy_tree(os.path.join(scenario_dir, "after"), after)       # new or wholly rewritten files
        for e in spec.get("edits", []):
            path = os.path.join(after, e["file"])
            text = open(path, encoding="utf-8").read()
            if e["old"] not in text:
                raise RuntimeError("edit not applied, text not found in " + e["file"] + ": " + e["old"])
            with open(path, "w", encoding="utf-8") as f:
                f.write(text.replace(e["old"], e["new"]))
        edits = edits_between(before, after)
        report = ctx.analyze(before)
        findings = [f for f in report["findings"] if not f.get("accepted")]
        hits = [(f, n) for f in findings for ok, n in [predicted(f, edits)] if ok]

        edited_files = sorted(k for k in edits if not k.startswith("+"))
        summary = "Files edited: " + ", ".join(edited_files) + "\nFindings that predicted it:\n" + (
            "\n".join("  " + brief(f) + " - " + str(n) + " places edited" for f, n in hits) or "  (none)")
        if ctx.verbose:
            print(summary)

        for e in spec.get("predictedBy", []):
            ok = any(matches(f, e) for f, _ in hits)
            res.add("changes", name, "predicted by " + describe(e) + (" - " + e["why"] if e.get("why") else ""), ok, summary, e.get("gap"))
        for e in spec.get("notPredictedBy", []):
            bad = [f for f, _ in hits if matches(f, e)]
            res.add("changes", name, "not predicted by " + describe(e) + (" - " + e["why"] if e.get("why") else ""), not bad,
                    "\n".join(brief(f) for f in bad), e.get("gap"))
        if spec.get("expectNoPrediction"):
            res.add("changes", name, "no finding claims this local change", not hits, summary)
        for e in spec.get("filesCovered", []):
            # Every edited file the change needed should be named by some predicting finding.
            files = e["files"]
            covered = {fl for f, _ in hits for fl in files_of(f)}
            missing = [x for x in files if x not in covered]
            res.add("changes", name, "edited files named by a finding: " + ", ".join(files) + (" - " + e["why"] if e.get("why") else ""),
                    not missing, "Not named: " + ", ".join(missing) + "\n" + summary, e.get("gap"))
        if "rank" in spec:
            # With the futures file in place, the finding that predicts the change should be near the top of the fix list.
            r = spec["rank"]
            order = [f for f in report["findings"] if not f.get("accepted")]   # already sorted by priority
            idx = next((i for i, f in enumerate(order) if matches(f, r["finding"])), None)
            ok = idx is not None and idx < r["top"]
            detail = "Rank " + (str(idx + 1) if idx is not None else "none") + ". Top of the list:\n" + "\n".join(
                "  " + str(i + 1) + ". " + brief(f) + " priority " + str(f["priority"]) for i, f in enumerate(order[:r["top"] + 2]))
            res.add("changes", name, "ranked in the top " + str(r["top"]) + ": " + describe(r["finding"]), ok, detail, r.get("gap"))


# ---------------------------------------------------------------- gate suite

def find_one(ctx, root, matcher):
    report = ctx.analyze(root)
    hits = [f for f in report["findings"] if matches(f, matcher)]
    if len(hits) != 1:
        raise RuntimeError("expected one finding matching " + describe(matcher) + ", got " + str(len(hits)) + ":\n" +
                           "\n".join(brief(f) for f in report["findings"]))
    return hits[0]


def run_gate(ctx, scenario_dir, spec, res, name):
    with tempfile.TemporaryDirectory() as tmp:
        root = source_root(scenario_dir, spec, tmp)
        git(root, "init", "-q")
        git(root, "add", "-A")
        git(root, "commit", "-qm", "init")
        ids = {}
        for i, step in enumerate(spec["steps"]):
            label = step.get("label") or ("step " + str(i + 1))
            try:
                if "baseline" in step:
                    ctx.run(["baseline", root, "--no-git"], cwd=root, check=True)
                elif "write" in step:
                    path = os.path.join(root, step["write"])
                    os.makedirs(os.path.dirname(path), exist_ok=True)
                    with open(path, "w") as f:
                        f.write(step["content"])
                elif "append" in step:
                    with open(os.path.join(root, step["append"]), "a") as f:
                        f.write(step["text"])
                elif "replace" in step:
                    path = os.path.join(root, step["replace"])
                    text = open(path).read()
                    old, new = step["old"], step["new"]
                    if old not in text:
                        raise RuntimeError("text to replace not found in " + step["replace"] + ": " + old)
                    with open(path, "w") as f:
                        f.write(text.replace(old, new))
                elif "stage" in step:
                    git(root, "add", "-A")
                elif "commit" in step:
                    git(root, "add", "-A")
                    git(root, "commit", "-qm", step["commit"], "--no-verify")
                elif "remember" in step:
                    ids[step["remember"]] = find_one(ctx, root, step["finding"])["fingerprint"]
                elif "decide" in step:
                    fid = ids[step["id"]] if "id" in step else find_one(ctx, root, step["finding"])["fingerprint"]
                    args = ["decide", fid, step["decide"], root]
                    if step.get("reason") is not None:
                        args += ["--reason", step["reason"]]
                    if step.get("future"):
                        args += ["--future", step["future"]]
                    p = ctx.run(args + ["--no-git"], cwd=root)
                    want = step.get("exit", 0)
                    res.add("gate", name, label, p.returncode == want,
                            "exit " + str(p.returncode) + ", wanted " + str(want) + "\n" + p.stdout + p.stderr)
                elif "reopenId" in step:
                    p = ctx.run(["reopen", ids[step["reopenId"]], root], cwd=root)
                    res.add("gate", name, label, p.returncode == 0, p.stdout + p.stderr)
                elif "run" in step:
                    p = ctx.run(step["run"] + [root] + (["--no-git"] if step.get("noGit", True) else []), cwd=root)
                    out = p.stdout + p.stderr
                    problems = []
                    if "exit" in step and p.returncode != step["exit"]:
                        problems.append("exit " + str(p.returncode) + ", wanted " + str(step["exit"]))
                    for s in step.get("contains", []):
                        if s not in out:
                            problems.append("output lacks: " + s)
                    for s in step.get("lacks", []):
                        if s in out:
                            problems.append("output should not contain: " + s)
                    res.add("gate", name, label, not problems, "\n".join(problems) + ("\n--- output ---\n" + out if problems else ""), step.get("gap"))
                elif "expectFinding" in step:
                    report = ctx.analyze(root)
                    hits = [f for f in report["findings"] if matches(f, step["expectFinding"])]
                    want = step.get("count", 1)
                    res.add("gate", name, label, len(hits) == want, "matched " + str(len(hits)) + ":\n" + "\n".join(brief(f) for f in hits))
                else:
                    raise RuntimeError("unknown step: " + json.dumps(step))
            except Exception as ex:
                res.add("gate", name, label, False, str(ex))
                return


# ---------------------------------------------------------------- main

SUITES = {"detection": run_detection, "changes": run_change, "gate": run_gate}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--suite", choices=sorted(SUITES))
    ap.add_argument("--filter", default="")
    ap.add_argument("--cli")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    ctx = Ctx(find_cli(a.cli), a.verbose)
    res = Results()
    for suite, fn in SUITES.items():
        if a.suite and a.suite != suite:
            continue
        for spec_path in sorted(glob.glob(os.path.join(HERE, suite, "*", "scenario.json"))):
            sdir = os.path.dirname(spec_path)
            name = os.path.basename(sdir)
            if a.filter and a.filter not in name:
                continue
            with open(spec_path) as f:
                spec = json.load(f)
            try:
                fn(ctx, sdir, spec, res, name)
            except Exception as ex:
                res.add(suite, name, "scenario ran", False, str(ex))
    res.print(a.verbose)
    bad = res.failed()
    if bad:
        print("\n" + str(len(bad)) + " check(s) failed.")
        return 1
    print("\nAll checks passed (known gaps don't fail the run).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
