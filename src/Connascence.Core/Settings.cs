using System.Collections.Generic;

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
        public IReadOnlyDictionary<string, double>? Strength { get; init; } = new Dictionary<string, double>
        {
            ["Name"] = 0, ["Type"] = 0, ["Meaning"] = 3, ["Position"] = 4, ["Algorithm"] = 5,
            ["Execution"] = 6, ["Timing"] = 7, ["Value"] = 8, ["Identity"] = 9,
        };

        /// <summary>Multiplier per distance. Strong coupling close together is cheap; across projects it is dear.</summary>
        public IReadOnlyDictionary<string, double>? DistanceWeight { get; init; } = new Dictionary<string, double>
        {
            ["SameMember"] = 0.1, ["SameType"] = 0.25, ["SameFile"] = 0.5, ["SameProject"] = 1.0, ["CrossProject"] = 2.0,
        };

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
        public IReadOnlyList<string> IgnoredNumbers { get; init; } = new List<string> { "0", "1", "-1", "2", "0.0", "1.0", "100", "0.5" };

        /// <summary>Member names that suggest a value is a code with hidden meaning (status, type, mode ...).</summary>
        public IReadOnlyList<string> CodeLikeNames { get; init; } = new List<string>
        {
            "status", "state", "type", "kind", "mode", "code", "role", "category", "level", "flag", "flags", "action", "op", "operation",
        };

        /// <summary>Names whose values are fixed by a language or platform, not the app (generic mode).</summary>
        public IReadOnlyList<string>? IgnoredSubjects { get; init; } = new List<string>
        {
            "readyState", "key", "keyCode", "which", "nodeType", "nodeName", "tagName", "__name__", "platform", "sys.platform", "os.name",
        };

        /// <summary>Method name prefixes that suggest "call me before anything else".</summary>
        public IReadOnlyList<string> InitMethodPrefixes { get; init; } = new List<string>
        {
            "Init", "Initialize", "Initialise", "Open", "Start", "Connect", "Begin", "Setup", "SetUp", "Configure", "Load", "Prepare",
        };

        public double StrengthOf(Kind kind)
        {
            double v;
            return Strength != null && Strength.TryGetValue(kind.ToString(), out v) ? v : (int)kind;
        }

        public double WeightOf(Distance d)
        {
            double v;
            return DistanceWeight != null && DistanceWeight.TryGetValue(d.ToString(), out v) ? v : 1.0;
        }
    }
}
