#!/bin/sh
# End-to-end test of the CLI workflow against the sample repo.
# Needs `connascence` on PATH. Usage: tests/e2e.sh [workdir]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
WORK=${1:-/tmp/connascence-e2e}
fail() { echo "FAIL: $1"; exit 1; }

rm -rf "$WORK" && cp -r "$HERE/samples/Shop" "$WORK" && cd "$WORK" || exit 1
git init -q && git config user.email e2e@example.com && git config user.name e2e
git add -A && git commit -qm init
for i in 1 2 3 4 5 6; do
  echo "// tweak $i" >> Shop.Orders/OrderService.cs; echo "// tweak $i" >> Shop.Web/OrdersController.cs
  git commit -qam "discount tweak $i"
  echo "// tweak $i" >> Shop.Orders/Order.cs; echo "# tweak $i" >> scripts/report.py
  git commit -qam "unrelated $i"
done

echo "== init"
connascence init . --hook >/dev/null || fail "init"
cat > .connascence/futures.json <<'EOF'
{ "product": "Shop",
  "futures": [
    { "id": "F1", "title": "Order status workflow grows", "likelihood": "high", "horizon": "next quarter", "source": "interview",
      "description": "Returns and partial shipping add new statuses.", "keywords": ["Status"] },
    { "id": "F2", "title": "Second payment provider", "likelihood": "medium", "source": "explore",
      "description": "Stripe alongside the current provider.", "keywords": ["PaymentClient"] },
    { "id": "F3", "title": "Multi-instance web tier", "likelihood": "medium", "source": "explore",
      "description": "More than one web server behind a load balancer.", "areas": ["Shop.Web/**"], "keywords": ["OrderCache"] } ] }
EOF
connascence baseline . --no-git || fail "baseline"
git add -A && git commit -qm "connascence setup" --no-verify

echo "== commit that grows existing couplings should be blocked by the hook"
cat >> Shop.Web/OrdersController.cs <<'EOF'
namespace Shop.Web
{
    public class Returns
    {
        public bool CanReturn(Shop.Orders.Order o) => o.Status == 3;
        public void Mark(Shop.Orders.Order o) { o.Status = 3; Shop.Orders.OrderCache.Items[o.Id] = o; }
    }
}
EOF
git add -A
if git commit -qm "returns" > /tmp/connascence-hook.out 2>&1; then cat /tmp/connascence-hook.out; fail "hook should have blocked the commit"; fi
grep -q "grew from 3 to 5 places" /tmp/connascence-hook.out || { cat /tmp/connascence-hook.out; fail "expected 'grew' label"; }
echo "blocked as expected"

echo "== decide: accept one, refactor the other"
STATUS_ID=$(connascence analyze . --no-git --format json | python3 -c "import json,sys; d=json.load(sys.stdin); print([f['fingerprint'] for f in d['findings'] if f['ruleId']=='CNM01' and f['groupKey'].startswith('num|3|')][0])")
connascence decide "$STATUS_ID" accept --reason "Status enum migration is planned as its own change (F1); not mixing it into the returns feature" --future F1 --by e2e || fail "accept"
# Refactor: stop touching the static cache from the new code. Take the id first; it disappears or changes after the fix.
CACHE_ID=$(connascence analyze . --no-git --format json | python3 -c "import json,sys; d=json.load(sys.stdin); print([f['fingerprint'] for f in d['findings'] if f['ruleId']=='CNI01'][0])")
python3 - <<'EOF'
p='Shop.Web/OrdersController.cs'
s=open(p).read().replace("o.Status = 3; Shop.Orders.OrderCache.Items[o.Id] = o;", "o.Status = 3;")
open(p,'w').write(s)
EOF
connascence decide "$CACHE_ID" refactor --reason "Returns no longer writes to OrderCache; F3 needs the cache out of statics" --future F3 --by e2e | grep -q "Shared mutable state" || fail "refactor decision should recover the title"
git add -A
git commit -qm "returns" > /tmp/connascence-hook.out 2>&1 || { cat /tmp/connascence-hook.out; fail "commit should pass after decisions"; }
echo "commit passed"
[ -z "$(git status --porcelain)" ] || { git status --short; fail "working tree should be clean after a commit"; }

echo "== history"
connascence history . | tail -8 || fail "history"

echo "== reports"
connascence analyze . --no-git --format json --out report.json >/dev/null 2>&1 && python3 -c "import json; json.load(open('report.json'))" || fail "json"
connascence analyze . --no-git --format sarif --out report.sarif >/dev/null 2>&1 && python3 -c "import json; d=json.load(open('report.sarif')); assert d['version']=='2.1.0'" || fail "sarif"
connascence analyze . --no-git --format markdown --out report.md >/dev/null 2>&1 && grep -q "Change probe" report.md || fail "markdown"
connascence analyze . --mode generic --no-git > generic.txt || fail "generic mode"
grep -q "GN" generic.txt || fail "generic mode found nothing"

echo "== calibrate"
connascence calibrate . --months 12 || fail "calibrate"
echo "ALL PASSED"
