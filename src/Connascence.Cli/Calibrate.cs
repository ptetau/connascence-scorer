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
            var linkedKind = new Dictionary<string, Kind>();
            foreach (var f in r.Findings)
            {
                var files = f.Files.OrderBy(x => x, StringComparer.Ordinal).ToList();
                for (int i = 0; i < files.Count; i++)
                    for (int j = i + 1; j < files.Count; j++)
                    {
                        var key = files[i] + "\n" + files[j];
                        double cur; linked.TryGetValue(key, out cur);
                        if (f.Cost > cur) { linked[key] = f.Cost; linkedKind[key] = f.Kind; }
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
            Console.WriteLine("File pairs linked by a finding that changed together at least once: " + linkedRate.ToString("0.0") + "% of " + linked.Count);
            Console.WriteLine("Random file pairs that changed together at least once:              " + randomRate.ToString("0.0") + "% of " + random.Count);
            var lift = randomRate > 0 ? linkedRate / randomRate : double.NaN;
            Console.WriteLine("Lift: " + (double.IsNaN(lift) ? "n/a" : lift.ToString("0.0") + "x"));
            Console.WriteLine();
            Console.WriteLine("By kind:");
            foreach (var g in linked.Keys.GroupBy(k => linkedKind[k]).OrderBy(g => (int)g.Key))
                Console.WriteLine("  " + g.Key.ToString().PadRight(10) + Rate(g).ToString("0.0").PadLeft(6) + "% of " + g.Count() + " pairs");

            var costly = linked.Where(p => p.Value >= config.Thresholds.MaxFindingCost).Select(p => p.Key).ToList();
            if (costly.Count > 0)
                Console.WriteLine("Findings at or above the gate (" + config.Thresholds.MaxFindingCost + "): " + Rate(costly).ToString("0.0") + "% of " + costly.Count + " pairs");

            Console.WriteLine();
            Console.WriteLine("How to read this: a lift well above 1 means findings point at code that really does change together.");
            Console.WriteLine("If a kind sits near the random rate, lower its strength in .connascence.json, or its detector is too noisy.");

            var hidden = pairCounts.Where(p => p.Value >= 3 && !linked.ContainsKey(p.Key)).OrderByDescending(p => p.Value).Take(10).ToList();
            if (hidden.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Often changed together but not linked by any finding (coupling the tool can't see):");
                foreach (var h in hidden) Console.WriteLine("  " + h.Value.ToString().PadLeft(3) + "x  " + h.Key.Replace("\n", "  <->  "));
            }
            return 0;
        }
    }
}
