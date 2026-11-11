using System.Collections.Generic;
using System.Linq;

namespace Connascence
{
    /// <summary>The nine forms of connascence, weakest first (Page-Jones / Weirich ordering).</summary>
    public enum Kind
    {
        Name = 1,
        Type = 2,
        Meaning = 3,
        Position = 4,
        Algorithm = 5,
        Execution = 6,
        Timing = 7,
        Value = 8,
        Identity = 9,
    }

    /// <summary>How far apart the coupled pieces of code are. Larger is further.</summary>
    public enum Distance
    {
        SameMember = 0,
        SameType = 1,
        SameFile = 2,
        SameProject = 3,
        CrossProject = 4,
    }

    /// <summary>One place in the code that takes part in a coupling.</summary>
    public sealed class CodeLocation
    {
        public string File { get; set; }          // path relative to the analysis root, forward slashes
        public int Line { get; set; }             // 1-based
        public int Column { get; set; }           // 1-based
        public int EndLine { get; set; }          // 1-based, 0 if a single point
        public string Project { get; set; }       // project or top-level folder
        public string Type { get; set; }          // containing type, or null
        public string Member { get; set; }        // containing member, or null
        public string Snippet { get; set; }       // the line of code, trimmed
        public string Role { get; set; }          // e.g. "declaration", "call site", "use"

        /// <summary>Stable identity used for fingerprints: ignores line numbers so edits elsewhere don't change it.</summary>
        public string Anchor => Project + "|" + File + "|" + (Type ?? "") + "|" + (Member ?? "");

        public override string ToString() => File + ":" + Line;
    }

    /// <summary>
    /// One instance of connascence that costs more than it should.
    /// Carries the cause (why it was flagged), a suggestion (how to weaken it),
    /// and a change probe (the question a reviewer should ask before deciding).
    /// </summary>
    public sealed class Finding
    {
        public string RuleId { get; set; }
        public Kind Kind { get; set; }
        public string Title { get; set; }

        /// <summary>Specific reason this was flagged, in plain words.</summary>
        public string Cause { get; set; }

        /// <summary>Specific refactoring that would weaken the coupling or bring it closer together.</summary>
        public string Suggestion { get; set; }

        /// <summary>The kind of future change that this coupling would make expensive.</summary>
        public string ChangeProbe { get; set; }

        public List<CodeLocation> Locations { get; set; } = new List<CodeLocation>();

        /// <summary>How many places must change together.</summary>
        public int Degree { get; set; }

        public Distance Distance { get; set; }

        /// <summary>Set by a detector when the other party is outside the code (e.g. another thread or service).</summary>
        public Distance? FixedDistance { get; set; }

        /// <summary>Key that identifies what is shared (e.g. the literal and what it is compared with).</summary>
        public string GroupKey { get; set; }

        // Filled in by scoring.
        public double Cost { get; set; }
        public string Fingerprint { get; set; }

        // Filled in by the CLI.
        public int Volatility { get; set; }       // commits touching these files in the look-back window
        public string VolatilityLevel { get; set; }
        public double Priority { get; set; }
        public bool Accepted { get; set; }
        public string AcceptedReason { get; set; }
        public bool InBaseline { get; set; }

        /// <summary>Compared with the baseline: null (no baseline or unchanged), "new", or "grew from N to M places".</summary>
        public string Change { get; set; }

        /// <summary>Set when every place sits in an area the owner said is stable (from futures.json).</summary>
        public string StableReason { get; set; }

        /// <summary>Titles of smaller findings folded into this one because they only exist inside its copies.</summary>
        public List<string> Subsumed { get; set; } = new List<string>();

        /// <summary>Ids of the likely futures (from futures.json) whose area this finding sits in.</summary>
        public List<string> Futures { get; set; } = new List<string>();

        public CodeLocation Primary => Locations.FirstOrDefault();
        public IEnumerable<string> Files => Locations.Select(l => l.File).Distinct();
    }
}
