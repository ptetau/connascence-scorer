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
        public string Title { get; set; }
        public string GroupKey { get; set; }
        public int Degree { get; set; }
        public double Cost { get; set; }
        /// <summary>Accepted when the baseline was taken: recorded for its size only, not as existing debt.</summary>
        public bool Accepted { get; set; }
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
        public List<Finding> Apply(IEnumerable<Finding> findings)
        {
            var accepted = Accepted().Accepted.GroupBy(a => a.Fingerprint).ToDictionary(g => g.Key, g => g.Last());
            var baseline = Baseline();
            var inBaseline = baseline == null ? new HashSet<string>() : new HashSet<string>(baseline.Fingerprints);
            var futuresFile = Futures();
            var futures = futuresFile.Futures;
            var stable = (futuresFile.Stable ?? new List<StableArea>())
                .Select(s => new { s.Reason, s.Keywords, Globs = s.Areas.Select(Glob.ToRegex).ToList() }).ToList();
            var matchers = futures.Select(f => new { Future = f, Globs = f.Areas.Select(Glob.ToRegex).ToList() }).ToList();

            return findings.Select(f =>
            {
                var isBaseline = inBaseline.Contains(f.Fingerprint);
                var before = baseline == null || isBaseline ? null
                    : baseline.Items.FirstOrDefault(b => b.Fingerprint == f.Fingerprint)
                      ?? baseline.Items.FirstOrDefault(b => b.RuleId == f.RuleId && b.GroupKey == f.GroupKey);
                var change = baseline == null || isBaseline || before?.Fingerprint == f.Fingerprint ? f.Change
                    : before == null ? "new"
                    : Growth(before, f);
                // An inline accept covers the coupling as it was when the baseline was taken. Once it grows, it needs a new look.
                var inlineLapsed = f.Accepted && (f.AcceptedReason ?? "").StartsWith("inline:", StringComparison.Ordinal)
                                   && change != null && change.StartsWith("grew", StringComparison.Ordinal);
                var stillAccepted = f.Accepted && !inlineLapsed;
                var accept = !stillAccepted && accepted.TryGetValue(f.Fingerprint, out var a) ? a : null;
                var futureIds = matchers
                    .Where(m => f.Locations.Any(l => m.Globs.Any(g => g.IsMatch(l.File))) || m.Future.Keywords.Any(k => k.Length > 0 && IsAbout(f, k)))
                    .Select(m => m.Future.Id);
                // Stable only if every place in the coupling sits in a stable area.
                var stableArea = stable.FirstOrDefault(s =>
                    f.Locations.All(l => s.Globs.Any(g => g.IsMatch(l.File)) || s.Keywords.Any(k => k.Length > 0 && Mentions(l, k))));
                return f with
                {
                    Accepted = stillAccepted || accept != null,
                    AcceptedReason = accept != null
                        ? accept.Reason + (string.IsNullOrEmpty(accept.Future) ? "" : " (considered future " + accept.Future + ")")
                        : stillAccepted ? f.AcceptedReason : null,
                    InBaseline = isBaseline,
                    Change = inlineLapsed ? change + "; the inline accept was for the smaller coupling" : change,
                    Futures = f.Futures.Concat(futureIds).ToList(),
                    StableReason = stableArea == null ? f.StableReason : stableArea.Reason ?? "marked stable",
                };
            }).ToList();
        }

        /// <summary>How a finding compares with its baseline entry: more places, a higher cost (places moved further apart), fewer places, or moved.</summary>
        static string Growth(BaselineItem before, Finding f) =>
            f.Degree > before.Degree ? "grew from " + before.Degree + " to " + f.Degree + " places"
            : f.Degree < before.Degree ? "shrank from " + before.Degree + " to " + f.Degree + " places"
            : f.Cost > before.Cost + 0.01 ? "grew in cost from " + Num(before.Cost) + " to " + Num(f.Cost) + " (same places count, further apart)"
            : "changed places";

        static string Num(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// True when a finding is about the keyword: it names the finding's subject, title, files, types or members,
        /// or it appears in the code of at least half its places. A word that turns up on one line of a large
        /// coupling (a Status check on a line that also reads a cache) doesn't make the coupling about it.
        /// </summary>
        static bool IsAbout(Finding f, string keyword)
        {
            var kw = Words(keyword);
            if (kw.Count == 0) return false;
            var structural = new[] { f.Title, f.GroupKey }.Concat(f.Locations.SelectMany(l => new[] { l.File, l.Type, l.Member }));
            if (structural.Any(t => HasWords(t, kw))) return true;
            var inCode = f.Locations.Count(l => HasWords(l.Snippet, kw));
            return inCode > 0 && inCode * 2 >= f.Locations.Count;
        }

        /// <summary>For stable areas: one place matches the keyword by name or in its code.</summary>
        static bool Mentions(CodeLocation l, string keyword)
        {
            var kw = Words(keyword);
            return kw.Count > 0 && new[] { l.File, l.Type, l.Member, l.Snippet }.Any(t => HasWords(t, kw));
        }

        static IReadOnlyList<string> Words(string text) => Text.Words(Regex.Replace(text ?? "", "[^A-Za-z0-9]+", " "));

        /// <summary>Whole-word match, case-insensitive: "InvoiceCache" matches "InvoiceCache.Items" and "invoice_cache"; "Pay" does not match "Payee".</summary>
        static bool HasWords(string text, IReadOnlyList<string> kw)
        {
            var words = Words(text);
            for (int i = 0; i + kw.Count <= words.Count; i++)
            {
                int j = 0;
                while (j < kw.Count && words[i + j] == kw[j]) j++;
                if (j == kw.Count) return true;
            }
            return false;
        }
    }
}
