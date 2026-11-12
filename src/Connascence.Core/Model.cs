using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;

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
    public sealed record CodeLocation
    {
        public string File { get; init; } = "";      // path relative to the analysis root, forward slashes
        public int Line { get; init; }               // 1-based
        public int Column { get; init; }             // 1-based
        public int EndLine { get; init; }             // 1-based, 0 if a single point
        public string Project { get; init; } = "";   // project or top-level folder
        public string? Type { get; init; }           // containing type, or null
        public string? Member { get; init; }         // containing member, or null
        public string Snippet { get; init; } = "";   // the line of code, trimmed
        public string Role { get; init; } = "";      // e.g. "declaration", "call site", "use"

        /// <summary>Stable identity used for fingerprints: ignores line numbers so edits elsewhere don't change it.</summary>
        public string Anchor => Project + "|" + File + "|" + (Type ?? "") + "|" + (Member ?? "");

        public override string ToString() => File + ":" + Line;
    }

    /// <summary>
    /// One instance of connascence that costs more than it should.
    /// Carries the cause (why it was flagged), a suggestion (how to weaken it),
    /// and a change probe (the question a reviewer should ask before deciding).
    /// </summary>
    /// <remarks>
    /// Immutable: scoring, merging and the CLI make new findings with <c>with</c>. Equality is by identity, not value:
    /// two findings with the same contents are still two findings (they are kept in sets and folded into each other).
    /// </remarks>
    public sealed record Finding
    {
        public string RuleId { get; init; } = "";
        public Kind Kind { get; init; }
        public string Title { get; init; } = "";

        /// <summary>Specific reason this was flagged, in plain words.</summary>
        public string Cause { get; init; } = "";

        /// <summary>Specific refactoring that would weaken the coupling or bring it closer together.</summary>
        public string Suggestion { get; init; } = "";

        /// <summary>The kind of future change that this coupling would make expensive.</summary>
        public string ChangeProbe { get; init; } = "";

        public IReadOnlyList<CodeLocation> Locations { get; init; } = ImmutableArray<CodeLocation>.Empty;

        /// <summary>How many places must change together.</summary>
        public int Degree { get; init; }

        public Distance Distance { get; init; }

        /// <summary>Set by a detector when the other party is outside the code (e.g. another thread or service).</summary>
        public Distance? FixedDistance { get; init; }

        /// <summary>Key that identifies what is shared (e.g. the literal and what it is compared with).</summary>
        public string? GroupKey { get; init; }

        // Filled in by scoring.
        public double Cost { get; init; }
        public string Fingerprint { get; init; } = "";

        // Filled in by the CLI.
        public int Volatility { get; init; }       // commits touching these files in the look-back window
        public string? VolatilityLevel { get; init; }
        public double Priority { get; init; }
        public bool Accepted { get; init; }
        public string? AcceptedReason { get; init; }
        public bool InBaseline { get; init; }

        /// <summary>Compared with the baseline: null (no baseline or unchanged), "new", or "grew from N to M places".</summary>
        public string? Change { get; init; }

        /// <summary>Set when every place sits in an area the owner said is stable (from futures.json).</summary>
        public string? StableReason { get; init; }

        /// <summary>Titles of smaller findings folded into this one because they only exist inside its copies.</summary>
        public IReadOnlyList<string> Subsumed { get; init; } = ImmutableArray<string>.Empty;

        /// <summary>Ids of the likely futures (from futures.json) whose area this finding sits in.</summary>
        public IReadOnlyList<string> Futures { get; init; } = ImmutableArray<string>.Empty;

        public CodeLocation? Primary => Locations.FirstOrDefault();
        public IEnumerable<string> Files => Locations.Select(l => l.File).Distinct();

        public bool Equals(Finding? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }
}
