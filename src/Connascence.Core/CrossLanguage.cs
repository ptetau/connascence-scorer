using System.Collections.Generic;
using System.Collections.Immutable;
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
        /// <summary>C# rule and the generic rule that reports the same thing, in the order they are merged.</summary>
        static readonly ImmutableArray<(string CSharp, string Generic)> Pairs =
            ImmutableArray.Create(("CNM01", "GNM01"), ("CNV02", "GNV02"));

        public static IReadOnlyList<Finding> Merge(IReadOnlyList<Finding> findings, Settings settings)
        {
            var merged = Pairs.Aggregate(Merged.None, (state, pair) => MergePair(findings, pair, state, settings));
            return findings.Where(f => !merged.Gone.Contains(f))
                .Select(f => merged.Replaced.TryGetValue(f, out var updated) ? updated : f)
                .ToImmutableArray();
        }

        /// <summary>Generic findings folded away, and C# findings with their new versions (both keyed by identity).</summary>
        sealed record Merged(ImmutableHashSet<Finding> Gone, ImmutableDictionary<Finding, Finding> Replaced)
        {
            public static readonly Merged None = new(ImmutableHashSet<Finding>.Empty, ImmutableDictionary<Finding, Finding>.Empty);
        }

        /// <summary>Folds each generic finding into the C# finding with the same key.</summary>
        static Merged MergePair(IReadOnlyList<Finding> findings, (string CSharp, string Generic) pair, Merged merged, Settings settings)
        {
            var generic = findings.Where(f => f.RuleId == pair.Generic && f.GroupKey != null)
                .GroupBy(f => Key(f.GroupKey!)).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
            return findings.Where(f => f.RuleId == pair.CSharp && f.GroupKey != null).ToImmutableArray()
                .Aggregate(merged, (state, cs) =>
                    generic.TryGetValue(Key(cs.GroupKey!), out var others) && others.FirstOrDefault(o => !state.Gone.Contains(o)) is { } other
                        ? new Merged(state.Gone.Add(other), state.Replaced.SetItem(cs, Absorb(cs, other, settings)))
                        : state);
        }

        /// <summary>The C# finding with the generic finding's places added, rescored.</summary>
        static Finding Absorb(Finding cs, Finding other, Settings settings)
        {
            var languages = other.Locations.Select(l => System.IO.Path.GetExtension(l.File).TrimStart('.')).Where(x => x.Length > 0).Distinct().ToImmutableArray();
            var degree = cs.Degree + other.Degree;
            return Scoring.Score(cs with
            {
                Locations = cs.Locations.Concat(other.Locations).ToImmutableArray(),
                Degree = degree,
                ChangeProbe = cs.ChangeProbe.Replace(Text.All(cs.Degree, "place"), Text.All(degree, "place")),
                Cause = cs.Cause + " The same value is also used " + Text.Spread(other.Locations) + " in " + Text.Join(languages) +
                        " code, which the C# compiler never sees, so a missed place there fails only at run time.",
            }, settings);
        }

        /// <summary>"num|3|order_status" and "num|3|orderstatus" are the same subject.</summary>
        static string Key(string groupKey)
        {
            var parts = groupKey.Split('|');
            return parts.Length < 3
                ? groupKey
                : string.Join("|", parts.Take(parts.Length - 1).Append(parts[parts.Length - 1].Replace("_", "").Replace("-", "").ToLowerInvariant()));
        }
    }
}
