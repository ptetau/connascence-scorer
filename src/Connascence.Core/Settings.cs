using System.Collections.Generic;

namespace Connascence
{
    /// <summary>
    /// Every weight and limit the tool uses. All of these are judgement calls,
    /// so all of them can be overridden in .connascence.json.
    /// </summary>
    public sealed class Settings
    {
        // ---- Scoring ----

        /// <summary>Strength rank per kind. Name and Type default to 0: they are the healthy kinds and are not scored.</summary>
        public Dictionary<string, double> Strength { get; set; } = new Dictionary<string, double>
        {
            ["Name"] = 0, ["Type"] = 0, ["Meaning"] = 3, ["Position"] = 4, ["Algorithm"] = 5,
            ["Execution"] = 6, ["Timing"] = 7, ["Value"] = 8, ["Identity"] = 9,
        };

        /// <summary>Multiplier per distance. Strong coupling close together is cheap; across projects it is dear.</summary>
        public Dictionary<string, double> DistanceWeight { get; set; } = new Dictionary<string, double>
        {
            ["SameMember"] = 0.1, ["SameType"] = 0.25, ["SameFile"] = 0.5, ["SameProject"] = 1.0, ["CrossProject"] = 2.0,
        };

        /// <summary>Cost grows as degree^DegreeExponent. 1 = linear (each extra place is one more edit).</summary>
        public double DegreeExponent { get; set; } = 1.0;

        /// <summary>Unbalanced cost per 1,000 lines that maps to a score of ~37 (score = 100 * e^(-load/scale)).</summary>
        public double ScoreScale { get; set; } = 100;

        // ---- Detector limits ----

        public int MinParametersForPosition { get; set; } = 4;
        public int MinSameTypeParameters { get; set; } = 2;
        public int MinTupleElements { get; set; } = 3;
        public int MinDuplicateStatements { get; set; } = 5;
        public int MinDuplicateLinesGeneric { get; set; } = 6;
        public int MinGenericParameters { get; set; } = 5;

        /// <summary>Numbers that are almost never "magic".</summary>
        public List<string> IgnoredNumbers { get; set; } = new List<string> { "0", "1", "-1", "2", "0.0", "1.0", "100", "0.5" };

        /// <summary>Member names that suggest a value is a code with hidden meaning (status, type, mode ...).</summary>
        public List<string> CodeLikeNames { get; set; } = new List<string>
        {
            "status", "state", "type", "kind", "mode", "code", "role", "category", "level", "flag", "flags", "action", "op", "operation",
        };

        /// <summary>Method name prefixes that suggest "call me before anything else".</summary>
        public List<string> InitMethodPrefixes { get; set; } = new List<string>
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
