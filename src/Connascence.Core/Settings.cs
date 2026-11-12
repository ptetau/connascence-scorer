using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

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
        public ValueMap<double> Strength { get; init; } = DefaultStrength;

        /// <summary>Multiplier per distance. Strong coupling close together is cheap; across projects it is dear.</summary>
        public ValueMap<double> DistanceWeight { get; init; } = DefaultDistanceWeight;

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
        public ValueList<string> IgnoredNumbers { get; init; } = ValueList.Of("0", "1", "-1", "2", "0.0", "1.0", "100", "0.5");

        /// <summary>Member names that suggest a value is a code with hidden meaning (status, type, mode ...).</summary>
        public ValueList<string> CodeLikeNames { get; init; } = ValueList.Of(
            "status", "state", "type", "kind", "mode", "code", "role", "category", "level", "flag", "flags", "action", "op", "operation");

        /// <summary>Names whose values are fixed by a language or platform, not the app (generic mode).</summary>
        public ValueList<string> IgnoredSubjects { get; init; } = ValueList.Of(
            "readyState", "key", "keyCode", "which", "nodeType", "nodeName", "tagName", "__name__", "platform", "sys.platform", "os.name");

        /// <summary>Method name prefixes that suggest "call me before anything else".</summary>
        public ValueList<string> InitMethodPrefixes { get; init; } = ValueList.Of(
            "Init", "Initialize", "Initialise", "Open", "Start", "Connect", "Begin", "Setup", "SetUp", "Configure", "Load", "Prepare");

        // In enum order, so the config `init` writes reads weakest to strongest.
        static readonly ValueMap<double> DefaultStrength = ValueMap.Of(
            ("Name", 0.0), ("Type", 0.0), ("Meaning", 3.0), ("Position", 4.0), ("Algorithm", 5.0), ("Execution", 6.0), ("Timing", 7.0), ("Value", 8.0), ("Identity", 9.0));

        static readonly ValueMap<double> DefaultDistanceWeight = ValueMap.Of(
            ("SameMember", 0.1), ("SameType", 0.25), ("SameFile", 0.5), ("SameProject", 1.0), ("CrossProject", 2.0));

        /// <summary>The configured strength for a kind (key case ignored), or the default when the config leaves it out.</summary>
        public double StrengthOf(Kind kind) =>
            Lookup(Strength, kind.ToString()) ?? Lookup(DefaultStrength, kind.ToString()) ?? (int)kind;

        /// <summary>The configured weight for a distance (key case ignored), or the default when the config leaves it out.</summary>
        public double WeightOf(Distance d) =>
            Lookup(DistanceWeight, d.ToString()) ?? Lookup(DefaultDistanceWeight, d.ToString()) ?? 1.0;

        static double? Lookup(ValueMap<double> map, string key) =>
            map.TryGetValue(key, out var exact) ? exact
            : map.Where(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Select(kv => (double?)kv.Value).FirstOrDefault();

        /// <summary>Keys in the strength and distance-weight maps that name no kind or distance, so they are ignored.</summary>
        public IEnumerable<string> UnknownKeys() =>
            Unknown<Kind>(Strength, "strength").Concat(Unknown<Distance>(DistanceWeight, "distanceWeight"));

        static IEnumerable<string> Unknown<TEnum>(ValueMap<double> map, string name) where TEnum : struct =>
            map.Keys
                .Where(k => !Enum.GetNames(typeof(TEnum)).Any(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase)))
                .Select(k => name + "." + k);
    }
}
