using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Connascence
{
    public sealed class KindSummary
    {
        public Kind Kind { get; set; }
        public int Findings { get; set; }
        public double Cost { get; set; }
    }

    public sealed class ScoreSummary
    {
        public int Lines { get; set; }
        public int Files { get; set; }
        public double TotalCost { get; set; }
        public double LoadPerKloc { get; set; }
        public int Score { get; set; }
        public int AcceptedFindings { get; set; }
        public double AcceptedCost { get; set; }
        public List<KindSummary> ByKind { get; set; } = new List<KindSummary>();
        public List<KeyValuePair<string, double>> ByFile { get; set; } = new List<KeyValuePair<string, double>>();
    }

    public static class Scoring
    {
        /// <summary>Distance between a set of locations = the widest gap between any two of them.</summary>
        public static Distance DistanceOf(IList<CodeLocation> locs)
        {
            if (locs.Count <= 1) return Distance.SameMember;
            if (locs.Select(l => l.Project).Distinct().Count() > 1) return Distance.CrossProject;
            var d = Distance.SameMember;
            var distinct = locs.GroupBy(l => l.Anchor + "|" + (l.Type == null ? l.Line.ToString() : "")).Select(g => g.First()).Take(200).ToList();
            for (int i = 0; i < distinct.Count; i++)
                for (int j = i + 1; j < distinct.Count; j++)
                {
                    var pair = Between(distinct[i], distinct[j]);
                    if (pair > d) d = pair;
                }
            return d;
        }

        static Distance Between(CodeLocation a, CodeLocation b)
        {
            if (a.Project != b.Project) return Distance.CrossProject;
            if (a.Type != null && a.Type == b.Type && a.Member != null && a.Member == b.Member) return Distance.SameMember;
            if (a.Type != null && a.Type == b.Type) return Distance.SameType;
            if (a.File == b.File) return Distance.SameFile;
            return Distance.SameProject;
        }

        /// <summary>Fills Distance (if not set by the detector), Degree, Cost and Fingerprint.</summary>
        public static void Score(Finding f, Settings s)
        {
            if (f.Degree <= 0) f.Degree = Math.Max(1, f.Locations.Count);
            f.Distance = f.FixedDistance ?? DistanceOf(f.Locations);
            f.Cost = Math.Round(s.StrengthOf(f.Kind) * Math.Pow(f.Degree, s.DegreeExponent) * s.WeightOf(f.Distance), 2);
            f.Fingerprint = FingerprintOf(f);
        }

        /// <summary>
        /// Identity of a finding that survives unrelated edits (no line numbers) but changes
        /// when the coupling itself changes (a new place joins it, or the shared thing changes).
        /// That way an accepted finding comes back for review once it grows.
        /// </summary>
        public static string FingerprintOf(Finding f)
        {
            var anchors = f.Locations.Select(l => l.Anchor).Distinct().OrderBy(x => x, StringComparer.Ordinal);
            var raw = f.RuleId + "\n" + f.GroupKey + "\n" + string.Join("\n", anchors.ToArray()) + "\n" + f.Degree;
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder();
                for (int i = 0; i < 5; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Shared clean-up after scoring: removes duplicate locations, and folds findings that only exist
        /// because code was copied into the finding for the copy. Fixing the copy fixes them too,
        /// so they are listed under it and their cost is added to it.
        /// </summary>
        public static List<Finding> Consolidate(List<Finding> findings)
        {
            foreach (var f in findings)
                f.Locations = f.Locations.GroupBy(l => l.File + ":" + l.Line + ":" + l.Role).Select(g => g.First()).ToList();

            var copies = findings.Where(f => f.Kind == Kind.Algorithm && f.Locations.All(l => l.EndLine > 0)).ToList();
            if (copies.Count == 0) return findings;
            var removed = new HashSet<Finding>();
            foreach (var f in findings)
            {
                if (copies.Contains(f)) continue;
                foreach (var c in copies)
                {
                    var regions = f.Locations.Select(l => c.Locations.FindIndex(r => r.File == l.File && l.Line >= r.Line && l.Line <= r.EndLine)).ToList();
                    if (regions.Any(i => i < 0) || regions.Distinct().Count() < 2) continue;
                    removed.Add(f);
                    c.Cost = Math.Round(c.Cost + f.Cost, 2);
                    c.Subsumed.Add(f.Title);
                    break;
                }
            }
            foreach (var c in copies.Where(c => c.Subsumed.Count > 0))
                c.Cause += " The copies also repeat: " + Text.Join(c.Subsumed.Distinct().ToList()) + ". Fixing the copy fixes those too.";
            return findings.Where(f => !removed.Contains(f)).ToList();
        }

        public static ScoreSummary Summarise(IList<Finding> findings, int lines, int files, Settings s)
        {
            var open = findings.Where(f => !f.Accepted).ToList();
            var total = open.Sum(f => f.Cost);
            var kloc = Math.Max(lines, 1) / 1000.0;
            var load = total / Math.Max(kloc, 0.2); // floor stops tiny repos swinging wildly
            var summary = new ScoreSummary
            {
                Lines = lines,
                Files = files,
                TotalCost = Math.Round(total, 1),
                LoadPerKloc = Math.Round(load, 1),
                Score = (int)Math.Round(100 * Math.Exp(-load / Math.Max(s.ScoreScale, 1))),
                AcceptedFindings = findings.Count(f => f.Accepted),
                AcceptedCost = Math.Round(findings.Where(f => f.Accepted).Sum(f => f.Cost), 1),
            };
            summary.ByKind = open.GroupBy(f => f.Kind)
                .Select(g => new KindSummary { Kind = g.Key, Findings = g.Count(), Cost = Math.Round(g.Sum(f => f.Cost), 1) })
                .OrderByDescending(k => k.Cost).ToList();

            // Spread each finding's cost evenly over the files it touches.
            var perFile = new Dictionary<string, double>();
            foreach (var f in open)
            {
                var fs = f.Files.ToList();
                foreach (var file in fs)
                {
                    double cur;
                    perFile.TryGetValue(file, out cur);
                    perFile[file] = cur + f.Cost / fs.Count;
                }
            }
            summary.ByFile = perFile.OrderByDescending(p => p.Value)
                .Select(p => new KeyValuePair<string, double>(p.Key, Math.Round(p.Value, 1))).ToList();
            return summary;
        }
    }
}
