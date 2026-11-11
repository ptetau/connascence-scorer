using System.Collections.Generic;

namespace Connascence
{
    /// <summary>Plain-English description of each kind, and the general way to weaken it.</summary>
    public static class Catalog
    {
        public sealed class Entry
        {
            public Kind Kind;
            public string Means;
            public string Weaken;
        }

        public static readonly IReadOnlyList<Entry> Kinds = new List<Entry>
        {
            new Entry { Kind = Kind.Name, Means = "Places must agree on a name.", Weaken = "This is the weakest kind. Rename tools handle it. Nothing to do." },
            new Entry { Kind = Kind.Type, Means = "Places must agree on a type.", Weaken = "The compiler checks it. Usually fine." },
            new Entry { Kind = Kind.Meaning, Means = "Places must agree on what a value means (3 = shipped, \"A\" = admin, true = draft).", Weaken = "Give the value a name in one place: an enum, a constant, or a small type. Then the compiler checks it." },
            new Entry { Kind = Kind.Position, Means = "Places must agree on order (argument order, tuple order, field order in a string).", Weaken = "Use names instead of positions: a small record, named arguments, or distinct types." },
            new Entry { Kind = Kind.Algorithm, Means = "Places must agree on how something is computed or encoded (a hash, a format, a copied rule).", Weaken = "Put the algorithm in one place and call it from everywhere." },
            new Entry { Kind = Kind.Execution, Means = "Places must do things in a set order (call Open before Send).", Weaken = "Make the wrong order impossible: set up in the constructor, or return a new type once set up." },
            new Entry { Kind = Kind.Timing, Means = "Places must agree on how long things take.", Weaken = "Wait for the real event, not a fixed delay. Keep any timings that remain in one policy." },
            new Entry { Kind = Kind.Value, Means = "Several values must change together to stay correct (a limit checked in two places).", Weaken = "Keep one source of truth and derive the rest from it." },
            new Entry { Kind = Kind.Identity, Means = "Places must share the very same object instance (static or global mutable state).", Weaken = "Give the state one owner and pass it in, or make it immutable." },
        };

        public static Entry Of(Kind k)
        {
            foreach (var e in Kinds) if (e.Kind == k) return e;
            return null;
        }

        /// <summary>Review guidance shown with every report. This is the lens, not a rule book.</summary>
        public const string ReviewLens =
@"Use this report as a lens, not a verdict. High connascence only costs something when the code has to change.

For each finding:
1. Read the change probe. Imagine the next few features, fixes or policy changes this code is likely to see.
2. Ask: would any plausible change have to touch several of the listed places in step?
3. If yes, refactor. Use the suggestion as a starting point. Aim to make the coupling weaker, or bring the places closer together.
4. If no (the code is stable, generated, a one-off, or the places always change together anyway), accept it and write down why.
   An accepted finding comes back for review if the coupling grows (a new place joins it).

Prefer the fix that is easiest to read. A little repetition that is obvious beats a clever abstraction that hides the rule.";

        /// <summary>Shown when files were analysed in generic mode.</summary>
        public const string GenericAdvice =
@"Some files were analysed in generic mode. Generic mode reads the code as text. It cannot see types, calls or scopes, so it only finds the obvious cases and its distances are rough (same file or not).

Things to check by hand in these files:
- Values with hidden meaning: status codes, magic strings, true/false arguments. Give them names in one place.
- Long argument lists of similar types passed by position. Use named arguments or a small type.
- Strings split and read by index. Parse them once into a named structure.
- The same format, regex, hash or rule written in more than one place. Keep one copy.
- Methods that must be called in order (init, open, connect). Make the wrong order impossible.
- Fixed sleeps or delays waiting for other work. Wait for the real event.
- Global or module-level state that several functions change. Give it one owner.";
    }
}
