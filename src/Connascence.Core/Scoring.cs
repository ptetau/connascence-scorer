using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Connascence
{
    public sealed record KindSummary
    {
        public Kind Kind { get; init; }
        public int Findings { get; init; }
        public double Cost { get; init; }
    }

    public sealed record ScoreSummary
    {
        public int Lines { get; init; }
        public int Files { get; init; }
        public double TotalCost { get; init; }
        public double LoadPerKloc { get; init; }
        public int Score { get; init; }
        public int AcceptedFindings { get; init; }
        public double AcceptedCost { get; init; }
        public IReadOnlyList<KindSummary> ByKind { get; set; } = new List<KindSummary>();
        public IReadOnlyList<KeyValuePair<string, double>> ByFile { get; set; } = new List<KeyValuePair<string, double>>();
    }

    public static class Scoring
    {
        /// <summary>Distance between a set of locations = the widest gap between any two of them.</summary>
        public static Distance DistanceOf(IReadOnlyList<CodeLocation> locs)
        {
            if (locs.Count <= 1) return Distance.SameMember;
            if (locs.Select(l => l.Project).Distinct().Count() > 1) return Distance.CrossProject;
            var d = Distance.SameMember;
            var distinct = locs.GroupBy(l => l.Anchor + "|" + (l.Type == null ? l.Line.ToString(CultureInfo.InvariantCulture) : "")).Select(g => g.First()).Take(200).ToList();
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

        /// <summary>The finding with Distance (if not set by the detector), Degree, Cost and Fingerprint filled in.</summary>
        public static Finding Score(Finding f, Settings s)
        {
            var degree = f.Degree <= 0 ? Math.Max(1, f.Locations.Count) : f.Degree;
            var distance = f.FixedDistance ?? DistanceOf(f.Locations);
            var scored = f with
            {
                Degree = degree,
                Distance = distance,
                Cost = Math.Round(s.StrengthOf(f.Kind) * Math.Pow(degree, s.DegreeExponent) * s.WeightOf(distance), 2),
            };
            return scored with { Fingerprint = FingerprintOf(scored) };
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
                for (int i = 0; i < 5; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Shared clean-up after scoring: removes duplicate locations, and folds findings that only exist
        /// because code was copied into the finding for the copy. Fixing the copy fixes them too,
        /// so they are listed under it and their cost is added to it.
        /// </summary>
        public static IReadOnlyList<Finding> Consolidate(IReadOnlyList<Finding> findings)
        {
            var deduped = findings
                .Select(f => f with { Locations = f.Locations.GroupBy(l => l.File + ":" + l.Line + ":" + l.Role).Select(g => g.First()).ToImmutableArray() })
                .ToImmutableArray();
            var copies = deduped.Where(f => f.Kind == Kind.Algorithm && f.Locations.All(l => l.EndLine > 0)).ToImmutableArray();
            if (copies.IsEmpty) return deduped;

            // Each other finding that lies inside two or more regions of a copy belongs to the first such copy.
            var owners = deduped.Where(f => !copies.Contains(f))
                .Select(f => (Finding: f, Copy: copies.FirstOrDefault(c => LiesInside(f, c))))
                .Where(x => x.Copy != null)
                .ToImmutableArray();
            var folded = owners.Select(x => x.Finding).ToImmutableHashSet();
            return deduped.Where(f => !folded.Contains(f))
                .Select(f => copies.Contains(f) ? Absorb(f, owners.Where(x => x.Copy == f).Select(x => x.Finding).ToImmutableArray()) : f)
                .ToImmutableArray();
        }

        /// <summary>True when the finding's places fall inside the copy's regions, in at least two different copies.</summary>
        static bool LiesInside(Finding f, Finding copy)
        {
            var regions = f.Locations.Select(l => RegionOf(copy, l)).ToImmutableArray();
            return !regions.Any(i => i < 0) && regions.Distinct().Count() >= 2;
        }

        static int RegionOf(Finding copy, CodeLocation l) =>
            copy.Locations.Select((r, i) => (r, i)).Where(x => x.r.File == l.File && l.Line >= x.r.Line && l.Line <= x.r.EndLine)
                .Select(x => x.i).DefaultIfEmpty(-1).First();

        /// <summary>The copy, with the findings it contains listed under it and their cost added to it.</summary>
        static Finding Absorb(Finding copy, ImmutableArray<Finding> contained)
        {
            if (contained.IsEmpty) return copy;
            var subsumed = copy.Subsumed.Concat(contained.Select(f => f.Title)).ToImmutableArray();
            return copy with
            {
                Cost = contained.Aggregate(copy.Cost, (cost, f) => Math.Round(cost + f.Cost, 2)),
                Subsumed = subsumed,
                Cause = copy.Cause + " The copies also repeat: " + Text.Join(subsumed.Distinct().ToImmutableArray()) + ". Fixing the copy fixes those too.",
            };
        }

        public static ScoreSummary Summarise(IReadOnlyList<Finding> findings, int lines, int files, Settings s)
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
