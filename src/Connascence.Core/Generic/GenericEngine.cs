using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

namespace Connascence.Generic
{
    public sealed record TextFile
    {
        public required string RelativePath { get; init; }
        public required string Project { get; init; }
        public required string Text { get; init; }
        public required string[] Lines { get; init; }
    }

    /// <summary>
    /// Language-agnostic text heuristics. Rougher than the C# analysis: no types, no symbols, no scopes.
    /// Rule ids start with G so they can be told apart from the C# rules.
    /// </summary>
    public static class GenericEngine
    {
        public static readonly ImmutableHashSet<string> SourceExtensions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            ".py", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".rb", ".php",
            ".swift", ".c", ".h", ".cpp", ".cc", ".hpp", ".m", ".fs", ".vb", ".dart", ".lua", ".ex", ".exs", ".cs", ".sql", ".sh", ".ps1");

        static readonly ImmutableHashSet<string> Keywords = ImmutableHashSet.Create(
            "if", "while", "for", "foreach", "switch", "catch", "return", "function", "def", "func", "fn", "new", "typeof", "sizeof",
            "elif", "when", "match", "using", "lock", "with", "assert", "print", "await", "yield", "not", "and", "or", "in", "is");

        const string Literal = @"-?\d+(?:\.\d+)?|""[^""\n]*""|'[^'\n]*'";
        const string Op = @"===|!==|==|!=|<=|>=|<|>";
        // A subject is a name (order.status) or a keyed lookup (row["status"], row['status']).
        const string Subject = @"(?<subject>[A-Za-z_$][\w.$]*(?:\[\s*[""'][\w$-]+[""']\s*\])?)";
        static readonly Regex SubjectThenLiteral = new Regex(Subject + @"\s*(?<op>" + Op + @")\s*(?<lit>" + Literal + @")(?![\w.])");
        static readonly Regex LiteralThenSubject = new Regex(@"(?<![\w.])(?<lit>" + Literal + @")\s*(?<op>" + Op + @")\s*" + Subject);
        static readonly Regex Assignment = new Regex(Subject + @"\s*(?<op>=)(?!=)\s*(?<lit>" + Literal + @")\s*[;,)]?\s*(?:#.*|//.*)?$");
        static readonly Regex TimerCall = new Regex(@"\b(?<call>setTimeout|setInterval)\s*\(.*,\s*(?<ms>\d[\d_]*)\s*\)");
        static readonly Regex Call = new Regex(@"(?<![\w.$])(?<fn>[A-Za-z_$][\w$]*)\s*\((?<args>[^()]*)\)");
        static readonly Regex Def = new Regex(@"\b(?:def|function|func|fn|sub|proc|fun)\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*\((?<params>[^)]*)\)");
        // The type slot can't be a keyword: "return f(1, 2)" and "new Order(id)" are calls, not definitions.
        static readonly Regex MethodDef = new Regex(@"^\s*(?:(?:public|private|protected|internal|static|final|override|async|virtual|abstract|export)\s+)*(?!(?:return|new|throw|await|yield|else|case|in|is|not|and|or|print|echo|delete|typeof|goto|then|do)\b)[\w<>\[\],.?]+\s+(?<name>[A-Za-z_]\w*)\s*\((?<params>[^()]*)\)\s*(?:throws [\w, .]+)?\s*\{?\s*$");
        static readonly Regex Sleep = new Regex(@"\b(?<call>time\.sleep|Thread\.sleep|Thread\.Sleep|Task\.Delay|usleep|sleep|setTimeout|delay)\s*\((?<args>[^()]*)\)");
        static readonly Regex PyGlobal = new Regex(@"^\s*global\s+(?<names>[\w, ]+)");
        static readonly Regex RubyGlobal = new Regex(@"(?<![\w])\$(?<name>[a-z_]\w*)\s*(?:=[^=]|\+=|-=|<<)");
        static readonly Regex Bool = new Regex(@"(?<![\w$=:])\s*(?<b>true|false|True|False)\s*$");


        public static IReadOnlyList<Finding> Analyze(IReadOnlyList<TextFile> files, Settings s)
        {
            var defs = Definitions(files);
            var findings = MagicValues(files, s)
                .Concat(BooleanFlags(files, defs))
                .Concat(LongParameterLists(files, defs, s))
                .Concat(DuplicateBlocks(files, s))
                .Concat(Sleeps(files))
                .Concat(Globals(files))
                .ToImmutableArray();

            var byPath = files.GroupBy(f => f.RelativePath).ToImmutableDictionary(g => g.Key, g => g.First().Lines);
            Func<string, string[]?> linesOf = p => byPath.TryGetValue(p, out var l) ? l : null;
            var scored = findings.Select(f => CSharp.CSharpEngine.Accepting(Scoring.Score(f, s), linesOf)).ToImmutableArray();
            return Scoring.Consolidate(scored).OrderByDescending(f => f.Cost).ToImmutableArray();
        }

        public static TextFile Load(string relativePath, string project, string text)
        {
            return new TextFile { RelativePath = relativePath, Project = project, Text = text, Lines = text.Replace("\r\n", "\n").Split('\n') };
        }

        static readonly ImmutableArray<string> CommentStarts = ImmutableArray.Create("//", "#", "*", "/*", "--", "'''", "\"\"\"");

        static bool IsComment(string trimmed) => CommentStarts.Any(c => trimmed.StartsWith(c, StringComparison.Ordinal));

        static CodeLocation Loc(TextFile f, int lineIndex, string role)
        {
            var full = f.Lines[lineIndex].Trim();
            var line = full.Length > 160 ? full.Substring(0, 157) + "..." : full;
            return new CodeLocation { File = f.RelativePath, Project = f.Project, Line = lineIndex + 1, Column = 1, Snippet = line, Role = role };
        }

        /// <summary>Lines that hold code (not blank, not comments), with their 0-based index.</summary>
        static IEnumerable<KeyValuePair<int, string>> CodeLines(TextFile f) =>
            f.Lines.Select((line, i) => new KeyValuePair<int, string>(i, line))
                .Where(cl => cl.Value.Trim() is var t && t.Length > 0 && !IsComment(t));

        // ---------- Definitions (to know which functions are "ours") ----------

        sealed record Definition
        {
            public required TextFile File { get; init; }
            public int Line { get; init; }
            public required string Name { get; init; }
            public required IReadOnlyList<string> Params { get; init; }
        }

        /// <summary>Function definitions by name: in the order each name is first defined, and looked up by name.</summary>
        sealed record DefinitionIndex(
            ImmutableArray<(string Name, ImmutableArray<Definition> Definitions)> InOrder,
            ImmutableDictionary<string, ImmutableArray<Definition>> ByName);

        static DefinitionIndex Definitions(IReadOnlyList<TextFile> files)
        {
            var inOrder = files.SelectMany(f => CodeLines(f).Select(cl => DefinitionAt(f, cl)))
                .OfType<Definition>()
                .GroupBy(d => d.Name)
                .Select(g => (Name: g.Key, Definitions: g.ToImmutableArray()))
                .ToImmutableArray();
            return new DefinitionIndex(inOrder, inOrder.GroupBy(x => x.Name).ToImmutableDictionary(g => g.Key, g => g.First().Definitions));
        }

        static Definition? DefinitionAt(TextFile f, KeyValuePair<int, string> cl)
        {
            var def = Def.Match(cl.Value);
            var m = def.Success ? def : MethodDef.Match(cl.Value);
            if (!m.Success) return null;
            var name = m.Groups["name"].Value;
            if (Keywords.Contains(name)) return null;
            var ps = m.Groups["params"].Value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)
                .Where(p => p != "self" && p != "cls" && p != "this" && !p.StartsWith("&self", StringComparison.Ordinal) && !p.StartsWith("*", StringComparison.Ordinal))
                .ToImmutableArray();
            return new Definition { File = f, Line = cl.Key, Name = name, Params = ps };
        }

        static bool IsDefinitionLine(string line) => Def.IsMatch(line) || MethodDef.IsMatch(line);

        // ---------- Magic values ----------

        /// <summary>One use of a literal with a subject: the group it belongs to, the subject as written, and where.</summary>
        sealed record Use(string Key, string Subject, CodeLocation Location);

        static IEnumerable<Finding> MagicValues(IReadOnlyList<TextFile> files, Settings s)
        {
            var ignored = s.IgnoredNumbers.ToImmutableHashSet();
            var codeLike = s.CodeLikeNames.ToImmutableHashSet();
            var platform = (s.IgnoredSubjects ?? ImmutableArray<string>.Empty).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            return files.SelectMany(f => CodeLines(f).SelectMany(cl => UsesOnLine(f, cl, ignored, codeLike, platform)))
                .GroupBy(u => u.Key)
                .Select(g => MagicValue(g.Key, g.First().Subject, g.Select(u => u.Location).ToImmutableArray(), ignored, codeLike))
                .OfType<Finding>();
        }

        /// <summary>The literals on a line compared with or assigned to a subject. Each literal counts once: the first pattern that matches it decides.</summary>
        static IEnumerable<Use> UsesOnLine(TextFile f, KeyValuePair<int, string> cl,
            ImmutableHashSet<string> ignored, ImmutableHashSet<string> codeLike, ImmutableHashSet<string> platform) =>
            SubjectThenLiteral.Matches(cl.Value).Cast<Match>()
                .Concat(LiteralThenSubject.Matches(cl.Value).Cast<Match>())
                .Concat(Assignment.Matches(cl.Value).Cast<Match>())
                .GroupBy(m => m.Groups["lit"].Index)
                .Select(g => UseOf(f, cl, g.First(), ignored, codeLike, platform))
                .OfType<Use>();

        static Use? UseOf(TextFile f, KeyValuePair<int, string> cl, Match m,
            ImmutableHashSet<string> ignored, ImmutableHashSet<string> codeLike, ImmutableHashSet<string> platform)
        {
            var lit = m.Groups["lit"].Value;
            var rawSubject = m.Groups["subject"].Value;
            var subject = SubjectName(rawSubject);
            // typeof x === "function" and document.readyState === "loading" compare with values the platform defines.
            if (platform.Contains(subject) || platform.Contains(rawSubject)) return null;
            var before = cl.Value.Substring(0, m.Groups["subject"].Index).TrimEnd();
            if (before.EndsWith("typeof", StringComparison.Ordinal)) return null;
            if (subject.Length == 0 || Keywords.Contains(subject) || subject == "null" || subject == "None" || subject == "nil") return null;
            bool codeLikeSubject = IsCodeLike(subject, codeLike);
            bool isString = lit.StartsWith("\"", StringComparison.Ordinal) || lit.StartsWith("'", StringComparison.Ordinal);
            var op = m.Groups["op"].Value;
            // Assigning a literal is only interesting when the target holds a code (status = 7), not for every x = 5.
            if (op == "=" && !codeLikeSubject) return null;
            if (isString && lit.Length <= 2) return null;
            // 0, 1, 2... are ordinary numbers unless compared with or assigned to a code.
            bool commonNumber = !isString && ignored.Contains(lit);
            if (commonNumber && !(codeLikeSubject && (op == "=" || op == "==" || op == "!=" || op == "===" || op == "!=="))) return null;
            bool relational = !(op == "==" || op == "!=" || op == "===" || op == "!==" || op == "=");
            if (isString && relational) return null; // "<" between strings is usually generics or markup, not a comparison
            var tag = !isString && relational ? "limit" : (isString ? "str" : "num");
            return new Use(tag + "|" + lit.Trim('"', '\'') + "|" + subject.ToLowerInvariant(), subject, Loc(f, cl.Key, Roles.Use));
        }

        static bool IsCodeLike(string subject, ImmutableHashSet<string> codeLike)
        {
            var words = Text.Words(subject);
            return words.Count > 0 && codeLike.Contains(words[words.Count - 1]);
        }

        static Finding? MagicValue(string key, string subject, ImmutableArray<CodeLocation> locs, ImmutableHashSet<string> ignored, ImmutableHashSet<string> codeLike)
        {
            var parts = key.Split('|');
            var value = parts[0] == "str" ? "\"" + Text.Quote(parts[1]) + "\"" : parts[1];
            bool codeSubject = IsCodeLike(subject, codeLike);
            bool limit = parts[0] == "limit";
            if (locs.Length < 2 && !(codeSubject && !limit)) return null;
            if (locs.Length < 2 && parts[0] == "num" && ignored.Contains(parts[1])) return null;

            return new Finding
            {
                RuleId = limit ? "GNV02" : "GNM01",
                Kind = limit ? Kind.Value : Kind.Meaning,
                Title = (limit ? "Repeated limit " : "Magic value ") + value,
                GroupKey = key,
                Locations = locs,
                Degree = locs.Length,
                Cause = limit
                    ? "The limit " + value + " is checked against " + subject + " " + Text.Spread(locs) + ". These checks must stay equal."
                    : "The value " + value + " is compared with or assigned to " + subject + " " + Text.Spread(locs) + ". Each place has to know what " + value + " means.",
                Suggestion = limit
                    ? "Name the limit once next to the code that owns the rule, and use that name in every check."
                    : "Give " + value + " a name in one place (an enum or constant) and use the name everywhere.",
                ChangeProbe = "If " + value + " changes meaning or value, will " + Text.All(locs.Length, "place") + " be found? Is that plausible?",
            };
        }

        /// <summary>order.status -> status; row["status"] -> status.</summary>
        static string SubjectName(string raw)
        {
            var b = raw.IndexOf('[');
            if (b >= 0) return raw.Substring(b).Trim('[', ']', ' ', '"', '\'');
            return raw.Substring(raw.LastIndexOf('.') + 1);
        }

        // ---------- Boolean flags ----------

        /// <summary>Calls to our own functions that pass a bare true/false, grouped by function.</summary>
        static IEnumerable<Finding> BooleanFlags(IReadOnlyList<TextFile> files, DefinitionIndex defs) =>
            files.SelectMany(f => CodeLines(f)
                    .Where(cl => !IsDefinitionLine(cl.Value))
                    .SelectMany(cl => Call.Matches(cl.Value).Cast<Match>()
                        .Where(m => !Keywords.Contains(m.Groups["fn"].Value) && defs.ByName.ContainsKey(m.Groups["fn"].Value))
                        .Where(m => m.Groups["args"].Value.Split(',').Any(a => Bool.IsMatch(a) && !a.Contains("=") && !a.Contains(":")))
                        .Select(m => (Function: m.Groups["fn"].Value, Location: Loc(f, cl.Key, Roles.CallSite)))))
                .GroupBy(x => x.Function, x => x.Location)
                .Select(g => defs.ByName.TryGetValue(g.Key, out var found) && found.Length > 0 ? BooleanFlag(g.Key, found[0], g.ToImmutableArray()) : null)
                .OfType<Finding>();

        static Finding BooleanFlag(string fn, Definition def, ImmutableArray<CodeLocation> calls)
        {
            var locs = calls.Prepend(Loc(def.File, def.Line, Roles.Declaration)).ToImmutableArray();
            return new Finding
            {
                RuleId = "GNM02",
                Kind = Kind.Meaning,
                Title = "Boolean flag argument to " + fn,
                GroupKey = fn,
                Locations = locs,
                Degree = calls.Length,
                Cause = Text.CountVerb(calls.Length, "caller", "passes", "pass") + " a bare true/false to " + fn + "(). Each caller has to know what it means.",
                Suggestion = "Split " + fn + " into two named functions, pass a named option or enum, or use a keyword/named argument.",
                ChangeProbe = "If a third option is needed or the flag's meaning changes, every caller must be revisited. Is that plausible?",
            };
        }

        // ---------- Long parameter lists ----------

        /// <summary>A call found in the text: which function, its argument list, and where.</summary>
        sealed record CallSite(string Function, string Args, TextFile File, int Line);

        static IEnumerable<Finding> LongParameterLists(IReadOnlyList<TextFile> files, DefinitionIndex defs, Settings s)
        {
            var candidates = defs.InOrder
                .Where(x => x.Definitions.Length == 1) // overloads make call matching unreliable
                .Select(x => x.Definitions[0])
                .Where(def => def.Params.Count >= s.MinGenericParameters)
                .ToImmutableArray();
            if (candidates.IsEmpty) return Enumerable.Empty<Finding>();

            // One pass over the code for every candidate's calls (a regex per function made this quadratic on large repos).
            var names = candidates.Select(d => d.Name).ToImmutableHashSet();
            var callsByName = files
                .SelectMany(f => CodeLines(f).SelectMany(cl => Call.Matches(cl.Value).Cast<Match>()
                    .Where(m => names.Contains(m.Groups["fn"].Value))
                    .Select(m => new CallSite(m.Groups["fn"].Value, m.Groups["args"].Value, f, cl.Key))))
                .GroupBy(c => c.Function)
                .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
            return candidates
                .Select(def => LongParameterList(def, callsByName.TryGetValue(def.Name, out var calls) ? calls : ImmutableArray<CallSite>.Empty))
                .OfType<Finding>();
        }

        static Finding? LongParameterList(Definition def, ImmutableArray<CallSite> sites)
        {
            var calls = sites
                .Where(c => !(c.File == def.File && c.Line == def.Line))
                .Where(c => c.Args.Split(',') is var args && args.Length >= 2 && !args.Skip(1).All(a => a.Contains("=") || a.Contains(":"))) // not all named
                .Select(c => Loc(c.File, c.Line, Roles.CallSite))
                .ToImmutableArray();
            if (calls.IsEmpty) return null;
            var locs = calls.Prepend(Loc(def.File, def.Line, Roles.Declaration)).ToImmutableArray();
            return new Finding
            {
                RuleId = "GNP01",
                Kind = Kind.Position,
                Title = def.Name + " takes " + def.Params.Count + " parameters by position",
                GroupKey = def.Name,
                Locations = locs,
                Degree = calls.Length,
                Cause = def.Name + " takes " + def.Params.Count + " parameters and " + Text.CountVerb(calls.Length, "caller", "passes", "pass") + " them by position. Swapping two of them may not be caught.",
                Suggestion = "Group related parameters into one object or record, or use named/keyword arguments at every call.",
                ChangeProbe = "If a parameter is added, removed or reordered, every positional caller must change in step. Is that likely?",
            };
        }

        // ---------- Duplicate blocks ----------

        /// <summary>A file's significant lines (normalised, with their line index) and the text of each window of k of them.</summary>
        sealed record Windows(TextFile File, ImmutableArray<KeyValuePair<int, string>> Lines, ImmutableArray<string> Keys);

        /// <summary>Where a window starts: the file and its index into the file's significant lines.</summary>
        readonly record struct At(Windows Source, int Index);

        static IEnumerable<Finding> DuplicateBlocks(IReadOnlyList<TextFile> files, Settings s)
        {
            int k = Math.Max(3, s.MinDuplicateLinesGeneric);
            var sources = files.Select(f => WindowsOf(f, k)).ToImmutableArray();
            var windows = sources
                .SelectMany(src => src.Keys.Select((key, i) => (Key: key, At: new At(src, i))))
                .GroupBy(x => x.Key, x => x.At)
                .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

            // Walk every window in file order. A window already inside a reported copy is skipped, so each copy is reported once, at its longest.
            return sources.SelectMany(src => src.Keys.Select((key, i) => (Key: key, At: new At(src, i))))
                .Aggregate((Covered: ImmutableHashSet<string>.Empty, Found: ImmutableList<Finding>.Empty), (state, w) =>
                {
                    if (!windows.TryGetValue(w.Key, out var occ) || occ.Length < 2 || state.Covered.Contains(w.At.Source.File.RelativePath + "#" + w.At.Index)) return state;
                    var distinct = NonOverlapping(occ, k);
                    if (distinct.Count < 2) return state;
                    var len = k + Enumerable.Range(1, int.MaxValue - 1).TakeWhile(offset => AllContinue(distinct, offset)).Count();
                    var covered = state.Covered.Union(distinct.SelectMany(o =>
                        Enumerable.Range(o.Index, len - k + 1).Select(j => o.Source.File.RelativePath + "#" + j)));
                    return (covered, state.Found.Add(CopiedBlock(w.Key, distinct, len)));
                })
                .Found;
        }

        static Windows WindowsOf(TextFile f, int k)
        {
            var lines = CodeLines(f)
                .Select(cl => new KeyValuePair<int, string>(cl.Key, Regex.Replace(cl.Value.Trim(), @"\s+", " ")))
                .Where(cl => cl.Value.Length > 3 && !Regex.IsMatch(cl.Value, @"^(import|using|#include|from|package|require|use)\b") && cl.Value.Trim('{', '}', '(', ')', ';', ' ', ']', '[', ',').Length > 0)
                .ToImmutableArray();
            var keys = Enumerable.Range(0, Math.Max(0, lines.Length - k + 1))
                .Select(i => string.Join("\n", lines.Skip(i).Take(k).Select(x => x.Value).ToArray()))
                .ToImmutableArray();
            return new Windows(f, lines, keys);
        }

        /// <summary>The occurrences in file and line order, dropping any that overlap an earlier one in the same file.</summary>
        static ImmutableList<At> NonOverlapping(ImmutableArray<At> occ, int k) =>
            occ.OrderBy(o => o.Source.File.RelativePath, StringComparer.Ordinal).ThenBy(o => o.Index)
                .Aggregate(ImmutableList<At>.Empty, (kept, o) =>
                    kept.Any(d => d.Source == o.Source && Math.Abs(d.Index - o.Index) < k) ? kept : kept.Add(o));

        /// <summary>True when every copy's window <paramref name="offset"/> steps further on is the same text: the copies continue.</summary>
        static bool AllContinue(ImmutableList<At> copies, int offset)
        {
            var next = copies.Select(o => o.Index + offset < o.Source.Keys.Length ? o.Source.Keys[o.Index + offset] : null).ToImmutableArray();
            return !next.Any(n => n == null) && next.Distinct().Count() == 1;
        }

        static Finding CopiedBlock(string key, ImmutableList<At> distinct, int len)
        {
            var locs = distinct.Select(o => Loc(o.Source.File, o.Source.Lines[o.Index].Key, Roles.Copy) with { EndLine = o.Source.Lines[o.Index + len - 1].Key + 1 })
                .ToImmutableArray();
            return new Finding
            {
                RuleId = "GNA01",
                Kind = Kind.Algorithm,
                Title = len + " lines copied " + distinct.Count + " times",
                GroupKey = Text.StableHash(key) + "|" + len,
                Locations = locs,
                Degree = distinct.Count,
                Cause = "The same " + len + " lines of code appear " + Text.Spread(locs) + ". If the logic changes, every copy must change the same way.",
                Suggestion = "Move the logic into one function, owned by the concept it belongs to, and call it from each place.",
                ChangeProbe = "If this logic changes, will every copy be found? Is it likely to change?",
            };
        }

        // ---------- Timing ----------

        static IEnumerable<Finding> Sleeps(IReadOnlyList<TextFile> files) =>
            Scoring.NumberRepeats(files.SelectMany(f => CodeLines(f)
                .Where(cl => !IsDefinitionLine(cl.Value))
                .Select(cl => (Line: cl, Title: WaitTitle(cl.Value)))
                .Where(x => x.Title != null)
                .Select(x => FixedWait(f, x.Line, x.Title!))));

        /// <summary>How a fixed wait on this line reads in a title, such as "time.sleep(2)", or null if there is none.</summary>
        static string? WaitTitle(string line)
        {
            var m = Sleep.Match(line);
            if (m.Success) return m.Groups["call"].Value + "(" + Text.Quote(m.Groups["args"].Value) + ")";
            var t = TimerCall.Match(line);   // setTimeout(() => refresh(), 3000): the callback holds brackets
            return t.Success ? t.Groups["call"].Value + "(..., " + t.Groups["ms"].Value + ")" : null;
        }

        static Finding FixedWait(TextFile f, KeyValuePair<int, string> cl, string title)
        {
            var loc = Loc(f, cl.Key, Roles.Wait);
            return new Finding
            {
                RuleId = "GNT01",
                Kind = Kind.Timing,
                Title = title,
                GroupKey = loc.File + "|" + cl.Value.Trim(),
                Locations = ImmutableArray.Create(loc),
                Degree = 1,
                FixedDistance = Distance.SameProject,
                Cause = "This waits a fixed time, assuming other work will be done by then.",
                Suggestion = "Wait for the real event (a promise, future, channel or condition with a timeout). Keep any retry timings in one policy.",
                ChangeProbe = "If the other side gets slower, does this break? Is that likely?",
            };
        }

        // ---------- Globals ----------

        /// <summary>Python `global x` declarations and Ruby $x writes, per project and name, where there are two or more.</summary>
        static IEnumerable<Finding> Globals(IReadOnlyList<TextFile> files) =>
            files.SelectMany(f => CodeLines(f).SelectMany(cl => GlobalWrites(f, cl)))
                .GroupBy(x => x.Key, x => x.Location)
                .Select(g => g.ToImmutableArray() is var locs && locs.Length >= 2 ? Global(g.Key, locs) : null)
                .OfType<Finding>();

        static IEnumerable<(string Key, CodeLocation Location)> GlobalWrites(TextFile f, KeyValuePair<int, string> cl)
        {
            var pg = PyGlobal.Match(cl.Value);
            var python = pg.Success
                ? pg.Groups["names"].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Select(n => f.Project + "|" + n)
                : Enumerable.Empty<string>();
            var ruby = RubyGlobal.Matches(cl.Value).Cast<Match>().Select(rg => f.Project + "|$" + rg.Groups["name"].Value);
            return python.Concat(ruby).Select(key => (key, Loc(f, cl.Key, Roles.WritesGlobal)));
        }

        static Finding Global(string key, ImmutableArray<CodeLocation> locs)
        {
            var name = key.Split('|')[1];
            return new Finding
            {
                RuleId = "GNI01",
                Kind = Kind.Identity,
                Title = "Global " + name + " changed in " + locs.Length + " places",
                GroupKey = key,
                Locations = locs,
                Degree = locs.Length,
                Cause = "The global " + name + " is changed " + Text.Spread(locs) + ". These places all depend on the same shared value and on each other's changes.",
                Suggestion = "Give the state one owner (an object passed to whoever needs it), or return new values instead of changing a global.",
                ChangeProbe = "If you need two of these, or run things in parallel, every place must change. Is that plausible?",
            };
        }
    }
}
