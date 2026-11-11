using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connascence.Cli
{
    public sealed class Acceptance
    {
        public string Fingerprint { get; set; }
        public string RuleId { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
        public string Future { get; set; }
        public string By { get; set; }
        public DateTime Date { get; set; }
        public List<string> Where { get; set; } = new List<string>();
    }

    public sealed class AcceptedFile { public List<Acceptance> Accepted { get; set; } = new List<Acceptance>(); }

    public sealed class Baseline
    {
        public DateTime Date { get; set; }
        public int Score { get; set; }
        public List<string> Fingerprints { get; set; } = new List<string>();
        public List<BaselineItem> Items { get; set; } = new List<BaselineItem>();
    }

    public sealed class BaselineItem
    {
        public string Fingerprint { get; set; }
        public string RuleId { get; set; }
        public string GroupKey { get; set; }
        public int Degree { get; set; }
        public double Cost { get; set; }
    }

    /// <summary>A plausible change to the software. Written by the futures skills or by hand.</summary>
    public sealed class Future
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        /// <summary>high, medium or low.</summary>
        public string Likelihood { get; set; } = "medium";
        /// <summary>When it might happen, e.g. "next quarter", "1-2 years".</summary>
        public string Horizon { get; set; }
        /// <summary>Where this came from: "explore" (model judgement) or "interview" (owner said so).</summary>
        public string Source { get; set; }
        /// <summary>Path globs of code this change would touch, relative to the analysis root.</summary>
        public List<string> Areas { get; set; } = new List<string>();
        /// <summary>Words that identify the code this change would touch (type names, concepts).</summary>
        public List<string> Keywords { get; set; } = new List<string>();
    }

    /// <summary>A part of the code the owner expects to stay put (fixed external format, legacy, generated, settled rules).</summary>
    public sealed class StableArea
    {
        public string Reason { get; set; }
        public List<string> Areas { get; set; } = new List<string>();
        public List<string> Keywords { get; set; } = new List<string>();
    }

    public sealed class FuturesFile
    {
        public string Product { get; set; }
        public DateTime? Updated { get; set; }
        public List<Future> Futures { get; set; } = new List<Future>();
        public List<StableArea> Stable { get; set; } = new List<StableArea>();
    }

    public sealed class Decision
    {
        public DateTime Date { get; set; }
        public string Fingerprint { get; set; }
        public string Choice { get; set; }
        public string Reason { get; set; }
        public string Future { get; set; }
        public string By { get; set; }
        public string RuleId { get; set; }
        public string Title { get; set; }
        public double Cost { get; set; }
    }

    /// <summary>Everything kept under .connascence/ in the analysed repo.</summary>
    public sealed class Store
    {
        public Store(string root) { Root = root; Dir = Path.Combine(root, ".connascence"); }

        public string Root { get; }
        public string Dir { get; }
        string AcceptedPath => Path.Combine(Dir, "accepted.json");
        string BaselinePath => Path.Combine(Dir, "baseline.json");
        public string FuturesPath => Path.Combine(Dir, "futures.json");
        string RunsPath => Path.Combine(Dir, "log", "runs.jsonl");
        string DecisionsPath => Path.Combine(Dir, "log", "decisions.jsonl");

        T Read<T>(string path) where T : new()
        {
            if (!File.Exists(path)) return new T();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Config.Json) ?? new T();
        }

        void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(value, Config.Json) + "\n");
        }

        void Append(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var opts = new JsonSerializerOptions(Config.Json) { WriteIndented = false };
            File.AppendAllText(path, JsonSerializer.Serialize(value, opts) + "\n");
        }

        public AcceptedFile Accepted() => Read<AcceptedFile>(AcceptedPath);
        public void SaveAccepted(AcceptedFile a) => Write(AcceptedPath, a);
        public Baseline Baseline() => File.Exists(BaselinePath) ? Read<Baseline>(BaselinePath) : null;
        public void SaveBaseline(Baseline b) => Write(BaselinePath, b);
        public FuturesFile Futures() => Read<FuturesFile>(FuturesPath);
        public void SaveFutures(FuturesFile f) => Write(FuturesPath, f);
        public bool HasFutures => File.Exists(FuturesPath);

        public void LogRun(object run) => Append(RunsPath, run);

        public void LogDecision(Decision d)
        {
            Append(DecisionsPath, new
            {
                date = d.Date, fingerprint = d.Fingerprint, decision = d.Choice, reason = d.Reason, future = d.Future,
                by = d.By, ruleId = d.RuleId, title = d.Title, cost = d.Cost,
            });
        }

        public List<JsonElement> Runs()
        {
            if (!File.Exists(RunsPath)) return new List<JsonElement>();
            return File.ReadAllLines(RunsPath).Where(l => l.Trim().Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        }

        public List<JsonElement> Decisions()
        {
            if (!File.Exists(DecisionsPath)) return new List<JsonElement>();
            return File.ReadAllLines(DecisionsPath).Where(l => l.Trim().Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        }

        /// <summary>Marks accepted and baseline findings, and tags each with the futures that would touch it.</summary>
        public void Apply(IList<Finding> findings)
        {
            var accepted = Accepted().Accepted.GroupBy(a => a.Fingerprint).ToDictionary(g => g.Key, g => g.Last());
            var baseline = Baseline();
            var inBaseline = baseline == null ? new HashSet<string>() : new HashSet<string>(baseline.Fingerprints);
            var futuresFile = Futures();
            var futures = futuresFile.Futures;
            var stable = (futuresFile.Stable ?? new List<StableArea>())
                .Select(s => new { s.Reason, s.Keywords, Globs = s.Areas.Select(Glob.ToRegex).ToList() }).ToList();
            var matchers = futures.Select(f => new { Future = f, Globs = f.Areas.Select(Glob.ToRegex).ToList() }).ToList();

            foreach (var f in findings)
            {
                Acceptance a;
                if (!f.Accepted && accepted.TryGetValue(f.Fingerprint, out a))
                {
                    f.Accepted = true;
                    f.AcceptedReason = a.Reason + (string.IsNullOrEmpty(a.Future) ? "" : " (considered future " + a.Future + ")");
                }
                f.InBaseline = inBaseline.Contains(f.Fingerprint);
                if (baseline != null && !f.InBaseline)
                {
                    var before = baseline.Items.FirstOrDefault(b => b.RuleId == f.RuleId && b.GroupKey == f.GroupKey);
                    f.Change = before == null ? "new"
                        : f.Degree > before.Degree ? "grew from " + before.Degree + " to " + f.Degree + " places"
                        : f.Degree < before.Degree ? "shrank from " + before.Degree + " to " + f.Degree + " places"
                        : "changed places";
                }
                foreach (var m in matchers)
                {
                    bool hit = f.Locations.Any(l =>
                        m.Globs.Any(g => g.IsMatch(l.File)) ||
                        m.Future.Keywords.Any(k => k.Length > 0 && Mentions(l, k)));
                    if (hit) f.Futures.Add(m.Future.Id);
                }
                // Stable only if every place in the coupling sits in a stable area.
                foreach (var s in stable)
                {
                    bool all = f.Locations.All(l => s.Globs.Any(g => g.IsMatch(l.File)) || s.Keywords.Any(k => k.Length > 0 && Mentions(l, k)));
                    if (all) { f.StableReason = s.Reason ?? "marked stable"; break; }
                }
            }
        }

        static bool Mentions(CodeLocation l, string keyword)
        {
            var hay = (l.File + " " + l.Type + " " + l.Member + " " + l.Snippet);
            return hay.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public static class Glob
    {
        /// <summary>Supports **, * and ? on forward-slash paths.</summary>
        public static Regex ToRegex(string glob)
        {
            var g = glob.Replace('\\', '/').Trim();
            if (g.StartsWith("./")) g = g.Substring(2);
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < g.Length; i++)
            {
                var c = g[i];
                if (c == '*')
                {
                    if (i + 1 < g.Length && g[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < g.Length && g[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            // A glob without wildcards that names a folder matches everything under it.
            if (!g.Contains("*") && !g.Contains("?") && !Path.HasExtension(g)) sb.Append("(?:/.*)?");
            sb.Append("$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }
    }
}
