using System;
using System.Collections.Generic;
using System.Linq;

namespace Connascence
{
    /// <summary>
    /// Links the same magic value or limit across languages. The C# engine and the generic (text) engine each
    /// report their own places; when both see the same value used with the same subject (status 3 in a C# service
    /// and in a Python script that reads the same table), that is one coupling, and the costliest kind to miss
    /// because no compiler sees both sides.
    /// </summary>
    public static class CrossLanguage
    {
        static readonly Dictionary<string, string> Pairs = new Dictionary<string, string>
        {
            ["CNM01"] = "GNM01",
            ["CNV02"] = "GNV02",
        };

        public static List<Finding> Merge(List<Finding> findings, Settings settings)
        {
            var removed = new HashSet<Finding>();
            foreach (var pair in Pairs)
            {
                var generic = findings.Where(f => f.RuleId == pair.Value && f.GroupKey != null)
                    .GroupBy(f => Key(f.GroupKey)).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var cs in findings.Where(f => f.RuleId == pair.Key && f.GroupKey != null).ToList())
                {
                    List<Finding> others;
                    if (!generic.TryGetValue(Key(cs.GroupKey), out others)) continue;
                    var other = others.FirstOrDefault(o => !removed.Contains(o));
                    if (other == null) continue;
                    removed.Add(other);

                    var languages = other.Locations.Select(l => System.IO.Path.GetExtension(l.File).TrimStart('.')).Where(x => x.Length > 0).Distinct().ToList();
                    cs.Locations.AddRange(other.Locations);
                    var before = cs.Degree;
                    cs.Degree += other.Degree;
                    if (cs.ChangeProbe != null)
                        cs.ChangeProbe = cs.ChangeProbe.Replace(Text.All(before, "place"), Text.All(cs.Degree, "place"));
                    cs.Cause += " The same value is also used " + Text.Spread(other.Locations) + " in " + Text.Join(languages) +
                                " code, which the C# compiler never sees, so a missed place there fails only at run time.";
                    Scoring.Score(cs, settings);
                }
            }
            return findings.Where(f => !removed.Contains(f)).ToList();
        }

        /// <summary>"num|3|order_status" and "num|3|orderstatus" are the same subject.</summary>
        static string Key(string groupKey)
        {
            var parts = groupKey.Split('|');
            if (parts.Length < 3) return groupKey;
            parts[parts.Length - 1] = parts[parts.Length - 1].Replace("_", "").Replace("-", "").ToLowerInvariant();
            return string.Join("|", parts);
        }
    }
}
