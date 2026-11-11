using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Connascence.Cli
{
    public static class Reports
    {
        public const string Version = "0.1.0";

        public static string Render(string format, AnalysisResult r, IList<Finding> shown, int top, string title = null)
        {
            switch ((format ?? "console").ToLowerInvariant())
            {
                case "json": return Json(r, shown);
                case "markdown":
                case "md":
                case "agent": return Markdown(r, shown, top, title);
                case "sarif": return Sarif(r, shown);
                default: return Console(r, shown, top, title);
            }
        }

        // ---------------- Console ----------------

        static string Console(AnalysisResult r, IList<Finding> shown, int top, string title)
        {
            var s = r.Summary;
            var sb = new StringBuilder();
            sb.AppendLine(title ?? "Connascence report");
            sb.AppendLine(new string('=', (title ?? "Connascence report").Length));
            sb.AppendLine("Score " + s.Score + "/100   load " + s.LoadPerKloc + " per 1k lines   " + s.Lines + " code lines in " + s.Files + " files");
            sb.AppendLine("Open findings " + r.Findings.Count(f => !f.Accepted) + "   accepted " + s.AcceptedFindings +
                          (r.VolatilityUsed ? "   volatility from git" : "") + (r.Futures.Count > 0 ? "   " + r.Futures.Count + " futures loaded" : ""));
            if (s.ByKind.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("By kind (weakest to strongest):");
                foreach (var k in s.ByKind.OrderBy(k => (int)k.Kind))
                    sb.AppendLine("  " + k.Kind.ToString().PadRight(10) + k.Findings.ToString().PadLeft(4) + " findings   cost " + k.Cost);
            }
            if (s.ByFile.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Costliest files:");
                foreach (var f in s.ByFile.Take(5)) sb.AppendLine("  " + f.Value.ToString("0.0").PadLeft(7) + "  " + f.Key);
            }

            var list = shown.Take(top).ToList();
            if (list.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Top " + list.Count + " of " + shown.Count + " (by priority):");
                int i = 1;
                foreach (var f in list)
                {
                    sb.AppendLine();
                    sb.AppendLine(i++ + ". [" + f.RuleId + "] " + f.Title);
                    sb.AppendLine("   " + f.Kind + ", degree " + f.Degree + ", " + Words(f.Distance) + "   cost " + f.Cost + "   priority " + f.Priority +
                                  "   id " + f.Fingerprint + Flags(f));
                    sb.AppendLine("   Why:      " + f.Cause);
                    sb.AppendLine("   Fix:      " + f.Suggestion);
                    sb.AppendLine("   Ask:      " + f.ChangeProbe);
                    if (f.Futures.Count > 0) sb.AppendLine("   Futures:  " + string.Join(", ", f.Futures.Select(id => FutureName(r, id))));
                    if (f.StableReason != null) sb.AppendLine("   Stable:   " + f.StableReason);
                    if (f.Accepted) sb.AppendLine("   Accepted: " + f.AcceptedReason);
                    foreach (var l in f.Locations.Take(6)) sb.AppendLine("     " + l.File + ":" + l.Line + "  " + l.Snippet);
                    if (f.Locations.Count > 6) sb.AppendLine("     ... and " + (f.Locations.Count - 6) + " more");
                }
            }
            if (r.GenericFiles > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Note: " + r.GenericFiles + " files (" + string.Join(", ", r.GenericLanguages) + ") were analysed in generic mode. Run with --format markdown for the generic checklist.");
            }
            foreach (var w in r.Warnings) sb.AppendLine("Warning: " + w);
            return sb.ToString();
        }

        static string Flags(Finding f)
        {
            var parts = new List<string>();
            if (f.VolatilityLevel != null) parts.Add("churn " + f.VolatilityLevel + " (" + f.Volatility + ")");
            if (f.InBaseline) parts.Add("baseline");
            if (f.Change != null) parts.Add(f.Change);
            if (f.Accepted) parts.Add("accepted");
            return parts.Count == 0 ? "" : "   [" + string.Join(", ", parts) + "]";
        }

        public static string Words(Distance d)
        {
            switch (d)
            {
                case Distance.SameMember: return "within one member";
                case Distance.SameType: return "within one type";
                case Distance.SameFile: return "within one file";
                case Distance.SameProject: return "across files in one project";
                default: return "across projects";
            }
        }

        static string FutureName(AnalysisResult r, string id)
        {
            var f = r.Futures.FirstOrDefault(x => x.Id == id);
            return f == null ? id : id + " " + f.Title + " (" + (f.Likelihood ?? "medium") + ")";
        }

        // ---------------- JSON ----------------

        static string Json(AnalysisResult r, IList<Finding> shown)
        {
            var doc = new
            {
                tool = "connascence",
                version = Version,
                root = r.Root,
                commit = r.Commit,
                date = DateTime.UtcNow,
                mode = r.Mode,
                summary = r.Summary,
                csharpFiles = r.CSharpFiles,
                genericFiles = r.GenericFiles,
                futures = r.Futures,
                findings = shown,
                warnings = r.Warnings,
            };
            return JsonSerializer.Serialize(doc, Config.Json);
        }

        // ---------------- Markdown for agents and reviewers ----------------

        static string Markdown(AnalysisResult r, IList<Finding> shown, int top, string title)
        {
            var s = r.Summary;
            var sb = new StringBuilder();
            sb.AppendLine("# " + (title ?? "Connascence review"));
            sb.AppendLine();
            sb.AppendLine("**Score " + s.Score + "/100.** Load " + s.LoadPerKloc + " per 1,000 lines. " + s.Lines + " code lines in " + s.Files +
                          " files. " + r.Findings.Count(f => !f.Accepted) + " open findings, " + s.AcceptedFindings + " accepted.");
            sb.AppendLine();
            sb.AppendLine("## How to use this");
            sb.AppendLine();
            sb.AppendLine(Catalog.ReviewLens);
            sb.AppendLine();
            sb.AppendLine("Record each decision so it is logged:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("connascence decide <id> refactor --reason \"<what you changed>\" [--future <future id>]");
            sb.AppendLine("connascence decide <id> accept   --reason \"<why no plausible change is hurt>\" [--future <future id>]");
            sb.AppendLine("```");

            if (r.Futures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Likely futures");
                sb.AppendLine();
                foreach (var f in r.Futures)
                    sb.AppendLine("- **" + f.Id + " " + f.Title + "** (" + (f.Likelihood ?? "medium") + (string.IsNullOrEmpty(f.Horizon) ? "" : ", " + f.Horizon) + "). " + f.Description);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("_No futures file. Run the futures skills to write `.connascence/futures.json`, so findings can be checked against changes you actually expect._");
            }

            sb.AppendLine();
            sb.AppendLine("## Findings");
            var list = shown.Take(top).ToList();
            if (list.Count == 0) { sb.AppendLine(); sb.AppendLine("None."); }
            foreach (var f in list)
            {
                sb.AppendLine();
                sb.AppendLine("### " + f.Title + " (`" + f.Fingerprint + "`)");
                sb.AppendLine();
                sb.AppendLine("- Rule " + f.RuleId + ", connascence of " + f.Kind.ToString().ToLowerInvariant() + ". Degree " + f.Degree + ", " + Words(f.Distance) +
                              ". Cost " + f.Cost + ", priority " + f.Priority + "." + (f.VolatilityLevel != null ? " Churn " + f.VolatilityLevel + " (" + f.Volatility + " commits)." : "") +
                              (f.InBaseline ? " In baseline." : "") + (f.Change != null ? " Since baseline: " + f.Change + "." : ""));
                sb.AppendLine("- **Why:** " + f.Cause);
                sb.AppendLine("- **Suggested fix:** " + f.Suggestion);
                sb.AppendLine("- **Change probe:** " + f.ChangeProbe);
                if (f.Futures.Count > 0) sb.AppendLine("- **Futures that touch this:** " + string.Join("; ", f.Futures.Select(id => FutureName(r, id))));
                if (f.StableReason != null) sb.AppendLine("- **Owner says this area is stable:** " + f.StableReason);
                if (f.Accepted) sb.AppendLine("- **Accepted:** " + f.AcceptedReason);
                sb.AppendLine("- **Where:**");
                foreach (var l in f.Locations.Take(10)) sb.AppendLine("  - `" + l.File + ":" + l.Line + "` " + (l.Role ?? "") + ": `" + l.Snippet.Replace("`", "'") + "`");
                if (f.Locations.Count > 10) sb.AppendLine("  - ... and " + (f.Locations.Count - 10) + " more");
            }
            if (shown.Count > list.Count) { sb.AppendLine(); sb.AppendLine("_" + (shown.Count - list.Count) + " lower-priority findings not shown. Use --top to see more._"); }

            if (r.GenericFiles > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Generic mode");
                sb.AppendLine();
                sb.AppendLine(r.GenericFiles + " files (" + string.Join(", ", r.GenericLanguages) + ") had no language-specific analyser.");
                sb.AppendLine();
                sb.AppendLine(Catalog.GenericAdvice);
            }

            sb.AppendLine();
            sb.AppendLine("## The kinds, weakest to strongest");
            sb.AppendLine();
            foreach (var e in Catalog.Kinds) sb.AppendLine("- **" + e.Kind + ":** " + e.Means + " " + e.Weaken);
            return sb.ToString();
        }

        // ---------------- SARIF (GitHub code scanning, Azure DevOps) ----------------

        static string Sarif(AnalysisResult r, IList<Finding> shown)
        {
            var rules = shown.GroupBy(f => f.RuleId).Select(g => new
            {
                id = g.Key,
                name = g.First().Kind + "Connascence",
                shortDescription = new { text = "Connascence of " + g.First().Kind.ToString().ToLowerInvariant() },
                fullDescription = new { text = Catalog.Of(g.First().Kind).Means + " " + Catalog.Of(g.First().Kind).Weaken },
            }).ToList();
            var results = shown.Select(f => new
            {
                ruleId = f.RuleId,
                level = f.Cost >= 20 ? "warning" : "note",
                message = new { text = f.Cause + " Fix: " + f.Suggestion + " Ask: " + f.ChangeProbe },
                partialFingerprints = new Dictionary<string, string> { ["connascence/v1"] = f.Fingerprint },
                locations = f.Locations.Take(1).Select(l => new
                {
                    physicalLocation = new { artifactLocation = new { uri = l.File }, region = new { startLine = l.Line, startColumn = l.Column } },
                }),
                relatedLocations = f.Locations.Skip(1).Take(20).Select((l, i) => new
                {
                    id = i + 1,
                    physicalLocation = new { artifactLocation = new { uri = l.File }, region = new { startLine = l.Line } },
                    message = new { text = l.Role },
                }),
                properties = new { cost = f.Cost, priority = f.Priority, degree = f.Degree, distance = f.Distance.ToString(), accepted = f.Accepted },
            });
            var sarif = new Dictionary<string, object>
            {
                ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
                ["version"] = "2.1.0",
                ["runs"] = new[]
                {
                    new { tool = new { driver = new { name = "connascence", version = Version, rules } }, results },
                },
            };
            return JsonSerializer.Serialize(sarif, Config.Json);
        }
    }
}
