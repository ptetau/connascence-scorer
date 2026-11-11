using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Connascence.Cli
{
    public static class Program
    {
        const string Help = @"connascence - scores coupling that makes code expensive to change, and suggests fixes.

Usage:
  connascence analyze  [path] [--format console|markdown|json|sarif] [--out file] [--top N] [--mode auto|csharp|generic] [--no-git] [--all]
  connascence check    [path] [--staged | --since <git ref> | --files a.cs,b.cs] [--max-cost N] [--min-score N] [--format ...]
  connascence decide   <id> accept|refactor --reason ""..."" [--future F1] [--by name] [path]
  connascence accept   <id> --reason ""..."" [--future F1] [--by name] [path]     (same as decide <id> accept)
  connascence reopen   <id> [path]                                                (removes an acceptance)
  connascence baseline [path]                  record current findings so 'check' only fails on new ones
  connascence history  [path]                  score trend, tracked findings and decisions over time
  connascence futures  [path]                  list the likely futures and how many findings each one touches
  connascence calibrate [path] [--months N]    test the scoring against what actually changed together in git
  connascence init     [path] [--hook]         write .connascence.json and a futures template; --hook adds a git pre-commit hook

Exit codes: 0 ok, 1 check failed, 2 usage or runtime error.";

        public static int Main(string[] argv)
        {
            try
            {
                var a = Args.Parse(argv);
                switch (a.Command)
                {
                    case "analyze": return Analyze(a);
                    case "check": return Check(a);
                    case "decide": return Decide(a, a.Positional(1), a.Positional(0), a.Positional(2));
                    case "accept": return Decide(a, "accept", a.Positional(0), a.Positional(1));
                    case "reopen": return Reopen(a);
                    case "baseline": return MakeBaseline(a);
                    case "history": return History(a);
                    case "futures": return FuturesCmd(a);
                    case "calibrate": return Calibrate.Run(a);
                    case "init": return Init(a);
                    case "version": case "--version": Console.WriteLine(Reports.Version); return 0;
                    default: Console.WriteLine(Help); return a.Command == null || a.Command == "help" || a.Command == "--help" || a.Command == "-h" ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 2;
            }
        }

        static string RootOf(string path) => Path.GetFullPath(string.IsNullOrEmpty(path) ? "." : path);

        static AnalysisResult Run(Args a, string root, out Config config)
        {
            config = Config.Load(root);
            return Workspace.Analyze(root, config, a.Get("mode"), !a.Has("no-git"));
        }

        static void Output(Args a, string text)
        {
            var outPath = a.Get("out");
            if (outPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllText(outPath, text);
                Console.Error.WriteLine("Wrote " + outPath);
            }
            else Console.WriteLine(text);
        }

        static int DefaultTop(Args a) => a.Int("top", (a.Get("format") ?? "console") == "console" ? 15 : 50);

        // ---------------- analyze ----------------

        static int Analyze(Args a)
        {
            var root = RootOf(a.Positional(0));
            Config config;
            var r = Run(a, root, out config);
            var shown = a.Has("all") ? r.Findings : r.Findings.Where(f => !f.Accepted).ToList();
            Output(a, Reports.Render(a.Get("format"), r, shown, DefaultTop(a)));
            LogRun(root, config, r, "analyze", null);
            return 0;
        }

        // ---------------- check (pre-commit / CI gate) ----------------

        static int Check(Args a)
        {
            var root = RootOf(a.Positional(0));
            Config config;
            var r = Run(a, root, out config);
            var maxCost = a.Double("max-cost", config.Thresholds.MaxFindingCost);
            var minScore = a.Int("min-score", config.Thresholds.MinScore);
            bool onlyNew = (config.Thresholds.FailOn ?? "new") == "new";

            List<string> changed = null;
            if (a.Has("staged") || a.Get("since") != null)
            {
                var git = Git.Open(root);
                if (git == null) throw new InvalidOperationException("--staged and --since need a git repository.");
                changed = a.Has("staged") ? git.StagedFiles() : git.ChangedSince(a.Get("since"));
            }
            else if (a.Get("files") != null)
                changed = a.Get("files").Split(',').Select(f => Path.GetRelativePath(root, Path.GetFullPath(f.Trim())).Replace('\\', '/')).ToList();

            var relevant = r.Findings.Where(f => !f.Accepted && !(onlyNew && f.InBaseline));
            if (changed != null)
            {
                var set = new HashSet<string>(changed);
                relevant = relevant.Where(f => f.Files.Any(set.Contains));
            }
            // Only coupling that is new or grew fails. Moving places around at the same size (a refactor that removes one
            // place and adds another) or shrinking it doesn't make it worse.
            var failing = relevant.Where(f => f.Cost >= maxCost && (f.Change == null || f.Change == "new" || f.Change.StartsWith("grew"))).ToList();
            bool scoreFails = minScore > 0 && r.Summary.Score < minScore;
            bool pass = failing.Count == 0 && !scoreFails;

            var title = pass ? "Connascence check passed" : "Connascence check failed";
            var format = a.Get("format") ?? "console";
            Output(a, Reports.Render(format, r, failing, a.Int("top", 50), title));
            if (!pass && format == "console")
            {
                if (scoreFails) Console.WriteLine("Score " + r.Summary.Score + " is below the minimum of " + minScore + ".");
                if (failing.Count > 0)
                {
                    Console.WriteLine(failing.Count + " finding(s) cost " + maxCost + " or more" + (changed != null ? " in the changed files" : "") + ".");
                    Console.WriteLine("For each one: imagine the changes this code is likely to see. If any would have to touch these places in step,");
                    Console.WriteLine("refactor using the suggestion. If not, accept it with a reason:");
                    Console.WriteLine("  connascence decide <id> accept --reason \"...\"");
                }
            }
            LogRun(root, config, r, "check", pass ? "pass" : "fail");
            return pass ? 0 : 1;
        }

        static void LogRun(string root, Config config, AnalysisResult r, string command, string result)
        {
            if (!config.Log.Enabled) return;
            try
            {
                new Store(root).LogRun(new
                {
                    date = DateTime.UtcNow,
                    command,
                    result,
                    commit = r.Commit,
                    score = r.Summary.Score,
                    load = r.Summary.LoadPerKloc,
                    lines = r.Summary.Lines,
                    open = r.Findings.Count(f => !f.Accepted),
                    accepted = r.Summary.AcceptedFindings,
                    byKind = r.Summary.ByKind.ToDictionary(k => k.Kind.ToString(), k => k.Cost),
                    tracked = r.Findings.Where(f => f.Cost >= config.Log.TrackCost).Select(f => new
                    {
                        id = f.Fingerprint, rule = f.RuleId, title = f.Title, cost = f.Cost, priority = f.Priority,
                        accepted = f.Accepted, futures = f.Futures, file = f.Primary?.File,
                    }),
                });
            }
            catch (Exception ex) { Console.Error.WriteLine("warning: could not write run log: " + ex.Message); }
        }

        // ---------------- decide / accept / reopen ----------------

        static int Decide(Args a, string choice, string id, string path)
        {
            if (string.IsNullOrEmpty(id) || (choice != "accept" && choice != "refactor"))
            {
                Console.Error.WriteLine("usage: connascence decide <id> accept|refactor --reason \"...\" [--future F1]");
                return 2;
            }
            var reason = a.Get("reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                Console.Error.WriteLine("A --reason is required. Say which plausible change you considered and why it is or isn't hurt.");
                return 2;
            }
            var root = RootOf(path);
            var store = new Store(root);
            Config config;
            var r = Run(a, root, out config);
            var f = r.Findings.FirstOrDefault(x => x.Fingerprint == id);
            var by = a.Get("by") ?? Environment.GetEnvironmentVariable("CONNASCENCE_ACTOR") ?? Environment.UserName;

            if (choice == "accept")
            {
                if (f == null)
                {
                    Console.Error.WriteLine("No current finding has id " + id + ". Run 'connascence analyze' to see current ids.");
                    return 2;
                }
                var file = store.Accepted();
                file.Accepted.RemoveAll(x => x.Fingerprint == id);
                file.Accepted.Add(new Acceptance
                {
                    Fingerprint = id, RuleId = f.RuleId, Title = f.Title, Reason = reason, Future = a.Get("future"), By = by, Date = DateTime.UtcNow,
                    Where = f.Locations.Select(l => l.File + ":" + l.Line).Take(10).ToList(),
                });
                store.SaveAccepted(file);
            }
            // After a refactor the finding is usually gone; recover what it was from the run log.
            string rule = f?.RuleId, title = f?.Title;
            double cost = f?.Cost ?? 0;
            if (f == null)
            {
                foreach (var run in Enumerable.Reverse(store.Runs()))
                {
                    if (!run.TryGetProperty("tracked", out var tracked)) continue;
                    var hit = tracked.EnumerateArray().FirstOrDefault(t => Str(t, "id") == id);
                    if (hit.ValueKind == JsonValueKind.Undefined) continue;
                    rule = Str(hit, "rule"); title = Str(hit, "title");
                    double.TryParse(Str(hit, "cost"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cost);
                    break;
                }
            }
            store.LogDecision(new Decision
            {
                Date = DateTime.UtcNow, Fingerprint = id, Choice = choice, Reason = reason, Future = a.Get("future"), By = by,
                RuleId = rule, Title = title, Cost = cost,
            });
            Console.WriteLine(choice == "accept"
                ? "Accepted " + id + " (" + f.Title + "). It comes back for review if the coupling grows."
                : "Logged refactor decision for " + id + (f != null ? " (" + f.Title + "). It is still present; it will drop out once the code changes." : title != null ? " (" + title + ")." : "."));
            return 0;
        }

        static int Reopen(Args a)
        {
            var id = a.Positional(0);
            var root = RootOf(a.Positional(1));
            var store = new Store(root);
            var file = store.Accepted();
            var removed = file.Accepted.RemoveAll(x => x.Fingerprint == id);
            store.SaveAccepted(file);
            store.LogDecision(new Decision { Date = DateTime.UtcNow, Fingerprint = id, Choice = "reopen", Reason = a.Get("reason"), By = a.Get("by") ?? Environment.UserName });
            Console.WriteLine(removed > 0 ? "Reopened " + id + "." : "No acceptance found for " + id + ".");
            return 0;
        }

        // ---------------- baseline ----------------

        static int MakeBaseline(Args a)
        {
            var root = RootOf(a.Positional(0));
            Config config;
            var r = Run(a, root, out config);
            new Store(root).SaveBaseline(new Baseline
            {
                Date = DateTime.UtcNow,
                Score = r.Summary.Score,
                Fingerprints = r.Findings.Where(f => !f.Accepted).Select(f => f.Fingerprint).Distinct().ToList(),
                Items = r.Findings.Where(f => !f.Accepted).Select(f => new BaselineItem
                {
                    Fingerprint = f.Fingerprint, RuleId = f.RuleId, GroupKey = f.GroupKey, Degree = f.Degree, Cost = f.Cost,
                }).ToList(),
            });
            Console.WriteLine("Baseline saved with " + r.Findings.Count(f => !f.Accepted) + " findings (score " + r.Summary.Score + "). 'check' will now only fail on new ones.");
            return 0;
        }

        // ---------------- history ----------------

        static int History(Args a)
        {
            var root = RootOf(a.Positional(0));
            var store = new Store(root);
            var runs = store.Runs();
            if (runs.Count == 0) { Console.WriteLine("No runs logged yet."); return 0; }

            Console.WriteLine("Date (UTC)          Commit    Command   Score  Load    Open  Accepted  Tracked");
            foreach (var run in runs.Skip(Math.Max(0, runs.Count - a.Int("top", 30))))
            {
                Console.WriteLine(
                    Str(run, "date").Replace("T", " ").PadRight(20).Substring(0, 19) + " " +
                    Str(run, "commit").PadRight(9) + " " +
                    Str(run, "command").PadRight(9) + " " +
                    Str(run, "score").PadLeft(5) + "  " +
                    Str(run, "load").PadLeft(6) + "  " +
                    Str(run, "open").PadLeft(4) + "  " +
                    Str(run, "accepted").PadLeft(8) + "  " +
                    (run.TryGetProperty("tracked", out var t) ? t.GetArrayLength().ToString() : "").PadLeft(7));
            }

            // What changed among tracked findings between the first and last run.
            var first = Tracked(runs.First());
            var last = Tracked(runs.Last());
            var fixedOnes = first.Keys.Except(last.Keys).ToList();
            var newOnes = last.Keys.Except(first.Keys).ToList();
            Console.WriteLine();
            Console.WriteLine("Since the first logged run: " + fixedOnes.Count + " tracked findings gone, " + newOnes.Count + " new.");
            foreach (var id in newOnes.Take(10)) Console.WriteLine("  new   " + id + "  " + last[id]);
            foreach (var id in fixedOnes.Take(10)) Console.WriteLine("  gone  " + id + "  " + first[id]);

            var decisions = store.Decisions();
            if (decisions.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Decisions: " + string.Join(", ", decisions.GroupBy(d => Str(d, "decision")).Select(g => g.Count() + " " + g.Key)));
                foreach (var d in decisions.Skip(Math.Max(0, decisions.Count - 10)))
                    Console.WriteLine("  " + Str(d, "date").Substring(0, 10) + "  " + Str(d, "decision").PadRight(8) + " " + Str(d, "fingerprint") + "  " +
                                      Str(d, "title") + "  - " + Str(d, "reason"));
            }
            return 0;
        }

        static Dictionary<string, string> Tracked(JsonElement run)
        {
            var d = new Dictionary<string, string>();
            if (run.TryGetProperty("tracked", out var t))
                foreach (var e in t.EnumerateArray()) d[Str(e, "id")] = Str(e, "title") + " (cost " + Str(e, "cost") + ")";
            return d;
        }

        static string Str(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return "";
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }

        // ---------------- futures ----------------

        static int FuturesCmd(Args a)
        {
            var root = RootOf(a.Positional(0));
            var store = new Store(root);
            if (!store.HasFutures)
            {
                Console.WriteLine("No futures file at " + store.FuturesPath + ". Run 'connascence init' for a template, or use the futures skills to write one.");
                return 0;
            }
            Config config;
            var r = Run(a, root, out config);
            foreach (var f in r.Futures)
            {
                var hits = r.Findings.Where(x => x.Futures.Contains(f.Id) && !x.Accepted).ToList();
                Console.WriteLine(f.Id + "  " + f.Title + "  [" + (f.Likelihood ?? "medium") + (string.IsNullOrEmpty(f.Horizon) ? "" : ", " + f.Horizon) + "]");
                Console.WriteLine("    " + f.Description);
                Console.WriteLine("    touches " + hits.Count + " open findings, total cost " + Math.Round(hits.Sum(h => h.Cost), 1) +
                                  (hits.Count > 0 ? ": " + string.Join(", ", hits.Take(5).Select(h => h.Fingerprint + " " + h.Title)) : ""));
                if (f.Areas.Count == 0 && f.Keywords.Count == 0) Console.WriteLine("    (no areas or keywords, so it can't match any code)");
            }
            return 0;
        }

        // ---------------- init ----------------

        static int Init(Args a)
        {
            var root = RootOf(a.Positional(0));
            var cfgPath = Path.Combine(root, Config.FileName);
            if (!File.Exists(cfgPath))
            {
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(new Config(), Config.Json) + "\n");
                Console.WriteLine("Wrote " + cfgPath);
            }
            else Console.WriteLine(cfgPath + " already exists.");

            var store = new Store(root);
            var ignore = Path.Combine(store.Dir, ".gitignore");
            if (!File.Exists(ignore))
            {
                Directory.CreateDirectory(store.Dir);
                // The run log changes on every check, so keep it local. Decisions, acceptances, baseline and futures are shared.
                File.WriteAllText(ignore, "log/runs.jsonl\n");
            }
            if (!store.HasFutures)
            {
                store.SaveFutures(new FuturesFile
                {
                    Product = Path.GetFileName(root),
                    Updated = DateTime.UtcNow,
                    Futures = new List<Future>
                    {
                        new Future
                        {
                            Id = "F1", Title = "Example: support a second database", Likelihood = "low", Horizon = "1-2 years", Source = "example",
                            Description = "Replace this with a real future. Use the futures skills to generate a list from your spec.",
                            Areas = new List<string> { "src/**/Data/**" }, Keywords = new List<string> { "SqlConnection", "Repository" },
                        },
                    },
                });
                Console.WriteLine("Wrote " + store.FuturesPath);
            }

            if (a.Has("hook"))
            {
                var git = Git.Open(root);
                var hooksDir = Path.Combine(root, ".git", "hooks");
                if (git == null || !Directory.Exists(hooksDir))
                {
                    Console.Error.WriteLine("No .git/hooks folder at " + root + ". Run init --hook from the repo root.");
                    return 2;
                }
                var hook = Path.Combine(hooksDir, "pre-commit");
                if (File.Exists(hook) && !a.Has("force"))
                {
                    Console.Error.WriteLine(hook + " already exists. Add 'connascence check --staged' to it, or rerun with --force.");
                    return 2;
                }
                File.WriteAllText(hook, PreCommitHook);
                if (!OperatingSystem.IsWindows())
                try { File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
                catch { }
                Console.WriteLine("Installed " + hook);
            }
            return 0;
        }

        public const string PreCommitHook = @"#!/bin/sh
# Connascence gate: fails the commit if staged files add costly, unaccepted coupling.
# Agents: read the report, then refactor or run `connascence decide <id> accept --reason ""...""`.
# Skip once with: git commit --no-verify
if ! command -v connascence >/dev/null 2>&1; then
  echo ""connascence not installed; skipping check (dotnet tool install -g Connascence.Tool)""
  exit 0
fi
connascence check --staged --no-git ""$(git rev-parse --show-toplevel)""
";
    }

    /// <summary>Tiny argument parser: command, positionals, --key value and --flag.</summary>
    public sealed class Args
    {
        public string Command;
        readonly List<string> _positional = new List<string>();
        readonly Dictionary<string, string> _options = new Dictionary<string, string>();

        static readonly HashSet<string> Flags = new HashSet<string> { "staged", "no-git", "all", "hook", "force" };

        public static Args Parse(string[] argv)
        {
            var a = new Args();
            for (int i = 0; i < argv.Length; i++)
            {
                var x = argv[i];
                if (x.StartsWith("--") && x.Length > 2)
                {
                    var name = x.Substring(2);
                    var eq = name.IndexOf('=');
                    if (eq > 0) { a._options[name.Substring(0, eq)] = name.Substring(eq + 1); continue; }
                    if (Flags.Contains(name) || i + 1 >= argv.Length || argv[i + 1].StartsWith("--")) a._options[name] = "true";
                    else a._options[name] = argv[++i];
                }
                else if (a.Command == null) a.Command = x;
                else a._positional.Add(x);
            }
            return a;
        }

        public string Positional(int i) => i < _positional.Count ? _positional[i] : null;
        public bool Has(string name) => _options.ContainsKey(name);
        public string Get(string name) { string v; return _options.TryGetValue(name, out v) ? v : null; }
        public int Int(string name, int fallback) { int v; return int.TryParse(Get(name), out v) ? v : fallback; }
        public double Double(string name, double fallback)
        {
            double v;
            return double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}
