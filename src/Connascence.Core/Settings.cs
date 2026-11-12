using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Connascence
{
    /// <summary>
    /// Every weight and limit the tool uses. All of these are judgement calls,
    /// so all of them can be overridden in .connascence.json.
    /// </summary>
    public sealed record Settings
    {
        // ---- Scoring ----

        /// <summary>Strength rank per kind. Name and Type default to 0: they are the healthy kinds and are not scored.</summary>
        public IReadOnlyDictionary<string, double>? Strength { get; init; } = ImmutableSortedDictionary.Create<string, double>(InEnumOrder<Kind>())
            .SetItem("Name", 0).SetItem("Type", 0).SetItem("Meaning", 3).SetItem("Position", 4).SetItem("Algorithm", 5).SetItem("Execution", 6).SetItem("Timing", 7).SetItem("Value", 8).SetItem("Identity", 9);

        /// <summary>Multiplier per distance. Strong coupling close together is cheap; across projects it is dear.</summary>
        public IReadOnlyDictionary<string, double>? DistanceWeight { get; init; } = ImmutableSortedDictionary.Create<string, double>(InEnumOrder<Distance>())
            .SetItem("SameMember", 0.1).SetItem("SameType", 0.25).SetItem("SameFile", 0.5).SetItem("SameProject", 1.0).SetItem("CrossProject", 2.0);

        /// <summary>Cost grows as degree^DegreeExponent. 1 = linear (each extra place is one more edit).</summary>
        public double DegreeExponent { get; init; } = 1.0;

        /// <summary>Unbalanced cost per 1,000 lines that maps to a score of ~37 (score = 100 * e^(-load/scale)).</summary>
        public double ScoreScale { get; init; } = 100;

        // ---- Detector limits ----

        public int MinParametersForPosition { get; init; } = 4;
        public int MinSameTypeParameters { get; init; } = 2;
        public int MinTupleElements { get; init; } = 3;
        public int MinDuplicateStatements { get; init; } = 5;
        public int MinDuplicateLinesGeneric { get; init; } = 6;
        public int MinGenericParameters { get; init; } = 5;

        /// <summary>Numbers that are almost never "magic".</summary>
        public IReadOnlyList<string> IgnoredNumbers { get; init; } = ImmutableArray.Create("0", "1", "-1", "2", "0.0", "1.0", "100", "0.5");

        /// <summary>Member names that suggest a value is a code with hidden meaning (status, type, mode ...).</summary>
        public IReadOnlyList<string> CodeLikeNames { get; init; } = ImmutableArray.Create(
            "status", "state", "type", "kind", "mode", "code", "role", "category", "level", "flag", "flags", "action", "op", "operation");

        /// <summary>Names whose values are fixed by a language or platform, not the app (generic mode).</summary>
        public IReadOnlyList<string>? IgnoredSubjects { get; init; } = ImmutableArray.Create(
            "readyState", "key", "keyCode", "which", "nodeType", "nodeName", "tagName", "__name__", "platform", "sys.platform", "os.name");

        /// <summary>Method name prefixes that suggest "call me before anything else".</summary>
        public IReadOnlyList<string> InitMethodPrefixes { get; init; } = ImmutableArray.Create(
            "Init", "Initialize", "Initialise", "Open", "Start", "Connect", "Begin", "Setup", "SetUp", "Configure", "Load", "Prepare");

        public double StrengthOf(Kind kind)
        {
            return Strength != null && Strength.TryGetValue(kind.ToString(), out var v) ? v : (int)kind;
        }

        public double WeightOf(Distance d)
        {
            return DistanceWeight != null && DistanceWeight.TryGetValue(d.ToString(), out var v) ? v : 1.0;
        }

        /// <summary>
        /// Orders keys that name an enum member (Kind, Distance) as the enum does, so the config `init` writes reads
        /// weakest to strongest. Other keys come last, in ordinal order.
        /// </summary>
        static IComparer<string> InEnumOrder<TEnum>() where TEnum : struct =>
            Comparer<string>.Create((a, b) => Rank<TEnum>(a) != Rank<TEnum>(b) ? Rank<TEnum>(a).CompareTo(Rank<TEnum>(b)) : string.CompareOrdinal(a, b));

        static int Rank<TEnum>(string name) where TEnum : struct =>
            Enum.TryParse<TEnum>(name, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
    }
}
