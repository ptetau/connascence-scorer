using System;
using System.Collections.Generic;
using System.Linq;

namespace Connascence.Cli
{
    /// <summary>
    /// Checks the scoring against reality. Files that keep changing in the same commits are coupled,
    /// whatever the code says. If findings point at file pairs that really do change together more
    /// often than random pairs, the weights are pointing the right way.
    /// </summary>
    public static class Calibrate
    {
        public static int Run(Args a)
        {
            var root = System.IO.Path.GetFullPath(a.Positional(0) ?? ".");
            var config = Config.Load(root);
            var git = Git.Open(root);
            if (git == null) { Console.Error.WriteLine("calibrate needs a git repository."); return 2; }
            var months = a.Int("months", 12);
            var commits = git.Commits(months).Where(c => c.Count > 1 && c.Count <= 40).ToList(); // huge commits are renames/formatting
            if (commits.Count < 10) { Console.WriteLine("Only " + commits.Count + " usable commits in " + months + " months. Not enough history to calibrate."); return 0; }

            var r = Workspace.Analyze(root, config, a.Get("mode"), false);
            var analysed = new HashSet<string>(r.Files);
            var pairCounts = new Dictionary<string, int>();
            foreach (var c in commits)
            {
                var files = c.Where(analysed.Contains).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count; i++)
                    for (int j = i + 1; j < files.Count; j++)
                    {
                        var key = files[i] + "\n" + files[j];
                        int n; pairCounts.TryGetValue(key, out n); pairCounts[key] = n + 1;
                    }
            }

            // Pairs linked by a finding, with the finding's cost.
            var linked = new Dictionary<string, double>();
            foreach (var f in r.Findings)
            {
                var files = f.Files.OrderBy(x => x, StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count; i++)
                    for (int j = i + 1; j < files.Count; j++)
                    {
                        var key = files[i] + "\n" + files[j];
                        double cur; linked.TryGetValue(key, out cur);
                        if (f.Cost > cur) linked[key] = f.Cost;
                    }
            }
            if (linked.Count == 0) { Console.WriteLine("No findings span more than one file, so there is nothing to compare with git history."); return 0; }

            double Rate(IEnumerable<string> pairs) { var l = pairs.ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => pairCounts.ContainsKey(p)) / l.Count; }

            var all = r.Files.Where(analysed.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var rnd = new Random(42);
            var random = new List<string>();
            for (int k = 0; k < 5000 && all.Count > 1; k++)
            {
                var x = all[rnd.Next(all.Count)]; var y = all[rnd.Next(all.Count)];
                if (x == y) continue;
                random.Add(string.CompareOrdinal(x, y) < 0 ? x + "\n" + y : y + "\n" + x);
            }

            var linkedRate = Rate(linked.Keys);
            var randomRate = Rate(random);
            Console.WriteLine("Calibration over " + commits.Count + " commits in the last " + months + " months");
            Console.WriteLine();
            Console.WriteLine("File pairs linked by a finding that changed together at least once: " + Percent(linkedRate) + " of " + linked.Count);
            Console.WriteLine("Random file pairs that changed together at least once:              " + Percent(randomRate) + " of " + random.Count);
            var lift = randomRate > 0 ? linkedRate / randomRate : double.NaN;
            Console.WriteLine("Lift: " + (double.IsNaN(lift) ? "n/a" : Number(lift) + "x"));

            // Per finding: of the findings whose files changed at all, how many had two of their files change in the same
            // commit. Counting findings rather than pairs keeps large findings (which make many pairs) from looking worse.
            var changedFiles = new HashSet<string>(commits.SelectMany(c => c));
            var outcomes = r.Findings.Where(f => f.Files.Count() > 1 && f.Files.Any(changedFiles.Contains)).Select(f =>
            {
                var files = f.Files.OrderBy(x => x, StringComparer.Ordinal).ToList();
                bool together = false;
                for (int i = 0; i < files.Count && !together; i++)
                    for (int j = i + 1; j < files.Count && !together; j++)
                        together = pairCounts.ContainsKey(files[i] + "\n" + files[j]);
                return new { Finding = f, Together = together };
            }).ToList();
            Console.WriteLine();
            if (outcomes.Count == 0)
                Console.WriteLine("None of the multi-file findings' files changed in this window.");
            else
            {
                string Line(string label, IEnumerable<bool> together)
                {
                    var l = together.ToList();
                    return "  " + label.PadRight(26) + Percent(100.0 * l.Count(t => t) / l.Count).PadLeft(7) + " of " + l.Count;
                }
                Console.WriteLine("Findings whose files changed, and that had two of their files change in the same commit:");
                Console.WriteLine(Line("All", outcomes.Select(o => o.Together)));
                foreach (var g in outcomes.GroupBy(o => o.Finding.Kind).OrderBy(g => (int)g.Key))
                    Console.WriteLine(Line(g.Key.ToString(), g.Select(o => o.Together)));
                var gate = config.Thresholds.MaxFindingCost;
                var below = outcomes.Where(o => o.Finding.Cost < gate).Select(o => o.Together).ToList();
                var above = outcomes.Where(o => o.Finding.Cost >= gate).Select(o => o.Together).ToList();
                if (below.Count > 0) Console.WriteLine(Line("Below the gate (< " + gate + ")", below));
                if (above.Count > 0) Console.WriteLine(Line("At or above the gate", above));
            }

            Console.WriteLine();
            Console.WriteLine("How to read this: a lift well above 1 means findings point at code that really does change together.");
            Console.WriteLine("If a kind's findings rarely change together, lower its strength in .connascence.json, or its detector is too noisy.");
            Console.WriteLine("Changing together shows how often coupling bites, not how bad a missed edit is, which is what strength measures.");

            var hidden = pairCounts.Where(p => p.Value >= 3 && !linked.ContainsKey(p.Key)).OrderByDescending(p => p.Value).Take(10).ToList();
            if (hidden.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Often changed together but not linked by any finding (coupling the tool can't see):");
                foreach (var h in hidden) Console.WriteLine("  " + h.Value.ToString().PadLeft(3) + "x  " + h.Key.Replace("\n", "  <->  "));
            }
            return 0;
        }

        static string Number(double value) => value.ToString("0.0");

        static string Percent(double value) => Number(value) + "%";
    }
}
