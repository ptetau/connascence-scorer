#!/usr/bin/env python3
"""FunctionalLint gate for Connascence.Core, with a ratchet for the migration.

Core was written imperatively. functionallint.policy lowers the rules it still breaks to warnings, and
.functionallint-baseline.json holds how many of each rule every file has. This gate fails when:
  - FunctionalLint reports an error (a rule that is already clean, nullable, culture, or a switched-off check), or
  - a file has more of a rule than its baseline count (new code must be functional).

    python3 scripts/functional-gate.py            check (pre-commit hook and CI)
    python3 scripts/functional-gate.py --update   lower the baseline to today's counts after fixing violations

--update only ever lowers counts (except when there is no baseline yet). It refuses to record growth: fix the new
violations instead.
Exit codes: 0 pass, 1 fail, 2 couldn't run.
"""
import json
import os
import re
import subprocess
import sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HEAL = os.path.join(ROOT, "external", "functional-csharp", "FunctionalLint", "heal.sh")
CORE = os.path.join("src", "Connascence.Core", "Connascence.Core.csproj")
BASELINE = os.path.join(ROOT, ".functionallint-baseline.json")
LINE = re.compile(r"^(?:(?P<file>.+?)\(\d+,\d+\): )?(?P<severity>warning|error) (?P<id>[A-Z]+\d+): (?P<message>.*)$")


def check():
    """Runs FunctionalLint on Core. Returns (counts per file and rule, error lines, warning lines by file and rule)."""
    if not os.path.exists(HEAL):
        print("functional gate: external/functional-csharp is missing. Run: git submodule update --init", file=sys.stderr)
        sys.exit(2)
    run = subprocess.run(["bash", HEAL, CORE, "--check"], cwd=ROOT, capture_output=True, text=True)
    if run.returncode not in (0, 1):
        print("functional gate: FunctionalLint couldn't run.\n" + run.stdout + run.stderr, file=sys.stderr)
        sys.exit(2)
    counts, errors, lines = Counter(), [], {}
    for raw in run.stdout.splitlines():
        m = LINE.match(raw.strip())
        if not m:
            continue
        if m["severity"] == "error":
            errors.append(raw)
            continue
        key = ((m["file"] or "(project)").replace("\\", "/"), m["id"])
        counts[key] += 1
        lines.setdefault(key, []).append(raw)
    return counts, errors, lines


def load_baseline():
    if not os.path.exists(BASELINE):
        return Counter()
    with open(BASELINE, encoding="utf-8") as f:
        data = json.load(f)
    return Counter({(file, rule): n for file, rules in data.items() for rule, n in rules.items()})


def save_baseline(counts):
    data = {}
    for (file, rule), n in sorted(counts.items()):
        if n > 0:
            data.setdefault(file, {})[rule] = n
    with open(BASELINE, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2, sort_keys=True)
        f.write("\n")


def main():
    update = "--update" in sys.argv
    first = not os.path.exists(BASELINE)
    counts, errors, lines = check()
    baseline = load_baseline()
    grew = sorted(k for k in counts if counts[k] > baseline.get(k, 0))
    shrank = sorted(k for k in baseline if counts.get(k, 0) < baseline[k])

    if errors:
        print("functional gate: FunctionalLint errors in Connascence.Core. These rules are already clean, so fix them:")
        print("\n".join(errors))
    if grew and not (update and first):
        print("functional gate: new FunctionalLint violations in Connascence.Core. Write the new code functionally:")
        for key in grew:
            file, rule = key
            print(f"  {file}: {rule} {baseline.get(key, 0)} -> {counts[key]}")
        print("Where they are (the new ones are among these):")
        for key in grew:
            print("\n".join("  " + line for line in lines[key]))
        print("Each message says what to write instead. Rule docs: external/functional-csharp/FunctionalLint/docs/rules/<ID>.md")
        print("Some rules auto-fix: external/functional-csharp/FunctionalLint/heal.sh " + CORE)
    if update:
        if grew and not first:
            print("functional gate: --update only lowers the baseline. Fix the growth above first.")
            return 1
        save_baseline(counts)
        print(f"functional gate: baseline updated ({sum(counts.values())} violations left in Connascence.Core).")
        clean = sorted({r for (_, r) in baseline} - {r for (_, r) in counts})
        if clean:
            print("  Now clean everywhere: " + ", ".join(clean) + ". Remove them from functionallint.policy so they become errors.")
        return 0
    if errors or grew:
        return 1
    if shrank:
        print(f"functional gate: passed, and {sum(baseline[k] - counts.get(k, 0) for k in shrank)} violation(s) were fixed. "
              "Lock that in: python3 scripts/functional-gate.py --update")
    else:
        print(f"functional gate: passed ({sum(counts.values())} known violations left in Connascence.Core).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
