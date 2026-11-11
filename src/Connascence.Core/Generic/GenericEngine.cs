using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Connascence.Generic
{
    public sealed class TextFile
    {
        public string RelativePath { get; set; }
        public string Project { get; set; }
        public string Text { get; set; }
        public string[] Lines { get; set; }
    }

    /// <summary>
    /// Language-agnostic text heuristics. Rougher than the C# analysis: no types, no symbols, no scopes.
    /// Rule ids start with G so they can be told apart from the C# rules.
    /// </summary>
    public static class GenericEngine
    {
        public static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".rb", ".php",
            ".swift", ".c", ".h", ".cpp", ".cc", ".hpp", ".m", ".fs", ".vb", ".dart", ".lua", ".ex", ".exs", ".cs", ".sql", ".sh", ".ps1",
        };

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "if", "while", "for", "foreach", "switch", "catch", "return", "function", "def", "func", "fn", "new", "typeof", "sizeof",
            "elif", "when", "match", "using", "lock", "with", "assert", "print", "await", "yield", "not", "and", "or", "in", "is",
        };

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
        static readonly Regex MethodDef = new Regex(@"^\s*(?:(?:public|private|protected|internal|static|final|override|async|virtual|abstract|export)\s+)*[\w<>\[\],.?]+\s+(?<name>[A-Za-z_]\w*)\s*\((?<params>[^()]*)\)\s*(?:throws [\w, .]+)?\s*\{?\s*$");
        static readonly Regex Sleep = new Regex(@"\b(?<call>time\.sleep|Thread\.sleep|Thread\.Sleep|Task\.Delay|usleep|sleep|setTimeout|delay)\s*\((?<args>[^()]*)\)");
        static readonly Regex PyGlobal = new Regex(@"^\s*global\s+(?<names>[\w, ]+)");
        static readonly Regex RubyGlobal = new Regex(@"(?<![\w])\$(?<name>[a-z_]\w*)\s*(?:=[^=]|\+=|-=|<<)");
        static readonly Regex Bool = new Regex(@"(?<![\w$=:])\s*(?<b>true|false|True|False)\s*$");

        public static List<Finding> Analyze(IList<TextFile> files, Settings s)
        {
            var findings = new List<Finding>();
            var defs = Definitions(files);
            findings.AddRange(MagicValues(files, s));
            findings.AddRange(BooleanFlags(files, defs));
            findings.AddRange(LongParameterLists(files, defs, s));
            findings.AddRange(DuplicateBlocks(files, s));
            findings.AddRange(Sleeps(files));
            findings.AddRange(Globals(files));

            var byPath = files.ToDictionary(f => f.RelativePath, f => f.Lines);
            Func<string, string[]> linesOf = p => { string[] l; return byPath.TryGetValue(p, out l) ? l : null; };
            foreach (var f in findings)
            {
                Scoring.Score(f, s);
                var reason = InlineAcceptance.ReasonFor(linesOf, f);
                if (reason != null) { f.Accepted = true; f.AcceptedReason = "inline: " + reason; }
            }
            return Scoring.Consolidate(findings).OrderByDescending(f => f.Cost).ToList();
        }

        public static TextFile Load(string relativePath, string project, string text)
        {
            return new TextFile { RelativePath = relativePath, Project = project, Text = text, Lines = text.Replace("\r\n", "\n").Split('\n') };
        }

        static bool IsComment(string trimmed) =>
            trimmed.StartsWith("//") || trimmed.StartsWith("#") || trimmed.StartsWith("*") || trimmed.StartsWith("/*") || trimmed.StartsWith("--") || trimmed.StartsWith("'''") || trimmed.StartsWith("\"\"\"");

        static CodeLocation Loc(TextFile f, int lineIndex, string role)
        {
            var line = f.Lines[lineIndex].Trim();
            if (line.Length > 160) line = line.Substring(0, 157) + "...";
            return new CodeLocation { File = f.RelativePath, Project = f.Project, Line = lineIndex + 1, Column = 1, Snippet = line, Role = role };
        }

        static IEnumerable<KeyValuePair<int, string>> CodeLines(TextFile f)
        {
            for (int i = 0; i < f.Lines.Length; i++)
            {
                var t = f.Lines[i].Trim();
                if (t.Length == 0 || IsComment(t)) continue;
                yield return new KeyValuePair<int, string>(i, f.Lines[i]);
            }
        }

        // ---------- Definitions (to know which functions are "ours") ----------

        sealed class Definition
        {
            public TextFile File;
            public int Line;
            public string Name;
            public List<string> Params;
        }

        static Dictionary<string, List<Definition>> Definitions(IList<TextFile> files)
        {
            var result = new Dictionary<string, List<Definition>>();
            foreach (var f in files)
            {
                foreach (var cl in CodeLines(f))
                {
                    var m = Def.Match(cl.Value);
                    if (!m.Success) m = MethodDef.Match(cl.Value);
                    if (!m.Success) continue;
                    var name = m.Groups["name"].Value;
                    if (Keywords.Contains(name)) continue;
                    var ps = m.Groups["params"].Value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)
                        .Where(p => p != "self" && p != "cls" && p != "this" && !p.StartsWith("&self") && !p.StartsWith("*") && !p.StartsWith("**")).ToList();
                    List<Definition> list;
                    if (!result.TryGetValue(name, out list)) { list = new List<Definition>(); result[name] = list; }
                    list.Add(new Definition { File = f, Line = cl.Key, Name = name, Params = ps });
                }
            }
            return result;
        }

        static bool IsDefinitionLine(string line) => Def.IsMatch(line) || MethodDef.IsMatch(line);

        // ---------- Magic values ----------

        static IEnumerable<Finding> MagicValues(IList<TextFile> files, Settings s)
        {
            var ignored = new HashSet<string>(s.IgnoredNumbers);
            var codeLike = new HashSet<string>(s.CodeLikeNames);
            var platform = new HashSet<string>(s.IgnoredSubjects ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<CodeLocation>>();
            var subjects = new Dictionary<string, string>();

            foreach (var f in files)
            {
                foreach (var cl in CodeLines(f))
                {
                    var seen = new HashSet<int>();
                    var matches = SubjectThenLiteral.Matches(cl.Value).Cast<Match>()
                        .Concat(LiteralThenSubject.Matches(cl.Value).Cast<Match>())
                        .Concat(Assignment.Matches(cl.Value).Cast<Match>());
                    foreach (Match m in matches)
                    {
                        var litGroup = m.Groups["lit"];
                        if (!seen.Add(litGroup.Index)) continue;
                        var lit = litGroup.Value;
                        var rawSubject = m.Groups["subject"].Value;
                        var subject = SubjectName(rawSubject);
                        // typeof x === "function" and document.readyState === "loading" compare with values the platform defines.
                        if (platform.Contains(subject) || platform.Contains(rawSubject)) continue;
                        var before = cl.Value.Substring(0, m.Groups["subject"].Index).TrimEnd();
                        if (before.EndsWith("typeof")) continue;
                        if (subject.Length == 0 || Keywords.Contains(subject) || subject == "null" || subject == "None" || subject == "nil") continue;
                        var subjectWords = Text.Words(subject);
                        bool codeLikeSubject = subjectWords.Count > 0 && codeLike.Contains(subjectWords[subjectWords.Count - 1]);
                        bool isString = lit.StartsWith("\"") || lit.StartsWith("'");
                        var op = m.Groups["op"].Value;
                        // Assigning a literal is only interesting when the target holds a code (status = 7), not for every x = 5.
                        if (op == "=" && !codeLikeSubject) continue;
                        if (isString && lit.Length <= 2) continue;
                        // 0, 1, 2... are ordinary numbers unless compared with or assigned to a code.
                        bool commonNumber = !isString && ignored.Contains(lit);
                        if (commonNumber && !(codeLikeSubject && (op == "=" || op == "==" || op == "!=" || op == "===" || op == "!=="))) continue;
                        bool relational = !(op == "==" || op == "!=" || op == "===" || op == "!==" || op == "=");
                        if (isString && relational) continue; // "<" between strings is usually generics or markup, not a comparison
                        var tag = !isString && relational ? "limit" : (isString ? "str" : "num");
                        var key = tag + "|" + lit.Trim('"', '\'') + "|" + subject.ToLowerInvariant();
                        List<CodeLocation> list;
                        if (!groups.TryGetValue(key, out list)) { list = new List<CodeLocation>(); groups[key] = list; subjects[key] = subject; }
                        list.Add(Loc(f, cl.Key, "use"));
                    }
                }
            }

            foreach (var kv in groups)
            {
                var locs = kv.Value;
                var subject = subjects[kv.Key];
                var parts = kv.Key.Split('|');
                var value = parts[0] == "str" ? "\"" + Text.Quote(parts[1]) + "\"" : parts[1];
                var words = Text.Words(subject);
                bool codeSubject = words.Count > 0 && codeLike.Contains(words[words.Count - 1]);
                bool limit = parts[0] == "limit";
                if (locs.Count < 2 && !(codeSubject && !limit)) continue;
                if (locs.Count < 2 && parts[0] == "num" && ignored.Contains(parts[1])) continue;

                yield return new Finding
                {
                    RuleId = limit ? "GNV02" : "GNM01",
                    Kind = limit ? Kind.Value : Kind.Meaning,
                    Title = (limit ? "Repeated limit " : "Magic value ") + value,
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = locs.Count,
                    Cause = limit
                        ? "The limit " + value + " is checked against " + subject + " " + Text.Spread(locs) + ". These checks must stay equal."
                        : "The value " + value + " is compared with or assigned to " + subject + " " + Text.Spread(locs) + ". Each place has to know what " + value + " means.",
                    Suggestion = limit
                        ? "Name the limit once next to the code that owns the rule, and use that name in every check."
                        : "Give " + value + " a name in one place (an enum or constant) and use the name everywhere.",
                    ChangeProbe = "If " + value + " changes meaning or value, will " + Text.All(locs.Count, "place") + " be found? Is that plausible?",
                };
            }
        }

        /// <summary>order.status -> status; row["status"] -> status.</summary>
        static string SubjectName(string raw)
        {
            var b = raw.IndexOf('[');
            if (b >= 0) return raw.Substring(b).Trim('[', ']', ' ', '"', '\'');
            return raw.Substring(raw.LastIndexOf('.') + 1);
        }

        // ---------- Boolean flags ----------

        static IEnumerable<Finding> BooleanFlags(IList<TextFile> files, Dictionary<string, List<Definition>> defs)
        {
            var groups = new Dictionary<string, List<CodeLocation>>();
            foreach (var f in files)
            {
                foreach (var cl in CodeLines(f))
                {
                    if (IsDefinitionLine(cl.Value)) continue;
                    foreach (Match m in Call.Matches(cl.Value))
                    {
                        var fn = m.Groups["fn"].Value;
                        if (Keywords.Contains(fn) || !defs.ContainsKey(fn)) continue;
                        var args = m.Groups["args"].Value.Split(',');
                        if (!args.Any(a => Bool.IsMatch(a) && !a.Contains("=") && !a.Contains(":"))) continue;
                        List<CodeLocation> list;
                        if (!groups.TryGetValue(fn, out list)) { list = new List<CodeLocation>(); groups[fn] = list; }
                        list.Add(Loc(f, cl.Key, "call site"));
                    }
                }
            }
            foreach (var kv in groups)
            {
                var def = defs[kv.Key][0];
                var locs = new List<CodeLocation> { Loc(def.File, def.Line, "declaration") };
                locs.AddRange(kv.Value);
                yield return new Finding
                {
                    RuleId = "GNM02",
                    Kind = Kind.Meaning,
                    Title = "Boolean flag argument to " + kv.Key,
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = kv.Value.Count,
                    Cause = Text.CountVerb(kv.Value.Count, "caller", "passes", "pass") + " a bare true/false to " + kv.Key + "(). Each caller has to know what it means.",
                    Suggestion = "Split " + kv.Key + " into two named functions, pass a named option or enum, or use a keyword/named argument.",
                    ChangeProbe = "If a third option is needed or the flag's meaning changes, every caller must be revisited. Is that plausible?",
                };
            }
        }

        // ---------- Long parameter lists ----------

        static IEnumerable<Finding> LongParameterLists(IList<TextFile> files, Dictionary<string, List<Definition>> defs, Settings s)
        {
            var callRegexCache = new Dictionary<string, Regex>();
            foreach (var kv in defs)
            {
                var def = kv.Value.FirstOrDefault(d => d.Params.Count >= s.MinGenericParameters);
                if (def == null || kv.Value.Count > 1) continue; // overloads make call matching unreliable
                var rx = new Regex(@"(?<![\w.$])" + Regex.Escape(kv.Key) + @"\s*\((?<args>[^()]*)\)");
                var calls = new List<CodeLocation>();
                foreach (var f in files)
                    foreach (var cl in CodeLines(f))
                    {
                        if (f == def.File && cl.Key == def.Line) continue;
                        foreach (Match m in rx.Matches(cl.Value))
                        {
                            var args = m.Groups["args"].Value.Split(',');
                            if (args.Length < 2) continue;
                            if (args.Skip(1).All(a => a.Contains("=") || a.Contains(":"))) continue; // all named
                            calls.Add(Loc(f, cl.Key, "call site"));
                        }
                    }
                if (calls.Count == 0) continue;
                var locs = new List<CodeLocation> { Loc(def.File, def.Line, "declaration") };
                locs.AddRange(calls);
                yield return new Finding
                {
                    RuleId = "GNP01",
                    Kind = Kind.Position,
                    Title = kv.Key + " takes " + def.Params.Count + " parameters by position",
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = calls.Count,
                    Cause = kv.Key + " takes " + def.Params.Count + " parameters and " + Text.CountVerb(calls.Count, "caller", "passes", "pass") + " them by position. Swapping two of them may not be caught.",
                    Suggestion = "Group related parameters into one object or record, or use named/keyword arguments at every call.",
                    ChangeProbe = "If a parameter is added, removed or reordered, every positional caller must change in step. Is that likely?",
                };
            }
        }

        // ---------- Duplicate blocks ----------

        static IEnumerable<Finding> DuplicateBlocks(IList<TextFile> files, Settings s)
        {
            int k = Math.Max(3, s.MinDuplicateLinesGeneric);
            var norm = new Dictionary<TextFile, List<KeyValuePair<int, string>>>();
            foreach (var f in files)
            {
                norm[f] = CodeLines(f)
                    .Select(cl => new KeyValuePair<int, string>(cl.Key, Regex.Replace(cl.Value.Trim(), @"\s+", " ")))
                    .Where(cl => cl.Value.Length > 3 && !Regex.IsMatch(cl.Value, @"^(import|using|#include|from|package|require|use)\b") && cl.Value.Trim('{', '}', '(', ')', ';', ' ', ']', '[', ',').Length > 0)
                    .ToList();
            }

            var windows = new Dictionary<string, List<KeyValuePair<TextFile, int>>>(); // hash -> (file, index into norm list)
            var hashAt = new Dictionary<TextFile, string[]>();
            foreach (var f in files)
            {
                var ls = norm[f];
                var hashes = new string[Math.Max(0, ls.Count - k + 1)];
                for (int i = 0; i + k <= ls.Count; i++)
                {
                    var key = string.Join("\n", ls.Skip(i).Take(k).Select(x => x.Value).ToArray());
                    hashes[i] = key;
                    List<KeyValuePair<TextFile, int>> list;
                    if (!windows.TryGetValue(key, out list)) { list = new List<KeyValuePair<TextFile, int>>(); windows[key] = list; }
                    list.Add(new KeyValuePair<TextFile, int>(f, i));
                }
                hashAt[f] = hashes;
            }

            var covered = new HashSet<string>();
            foreach (var f in files)
            {
                for (int i = 0; i < hashAt[f].Length; i++)
                {
                    var key = hashAt[f][i];
                    var occ = windows[key];
                    if (occ.Count < 2 || covered.Contains(f.RelativePath + "#" + i)) continue;
                    // Drop overlapping occurrences in the same file.
                    var distinct = new List<KeyValuePair<TextFile, int>>();
                    foreach (var o in occ.OrderBy(o => o.Key.RelativePath, StringComparer.Ordinal).ThenBy(o => o.Value))
                        if (!distinct.Any(d => d.Key == o.Key && Math.Abs(d.Value - o.Value) < k)) distinct.Add(o);
                    if (distinct.Count < 2) continue;

                    // Extend the duplicate while every copy keeps matching.
                    int len = k;
                    while (true)
                    {
                        var next = distinct.Select(o => o.Value + len - k + 1 < hashAt[o.Key].Length ? hashAt[o.Key][o.Value + len - k + 1] : null).ToList();
                        if (next.Any(n => n == null) || next.Distinct().Count() != 1) break;
                        len++;
                    }
                    foreach (var o in distinct)
                        for (int j = o.Value; j <= o.Value + len - k; j++) covered.Add(o.Key.RelativePath + "#" + j);

                    var locs = distinct.Select(o =>
                    {
                        var l = Loc(o.Key, norm[o.Key][o.Value].Key, "copy");
                        l.EndLine = norm[o.Key][o.Value + len - 1].Key + 1;
                        return l;
                    }).ToList();
                    yield return new Finding
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
            }
        }

        // ---------- Timing ----------

        static IEnumerable<Finding> Sleeps(IList<TextFile> files)
        {
            foreach (var f in files)
                foreach (var cl in CodeLines(f))
                {
                    if (IsDefinitionLine(cl.Value)) continue;
                    var m = Sleep.Match(cl.Value);
                    string title = null;
                    if (m.Success) title = m.Groups["call"].Value + "(" + Text.Quote(m.Groups["args"].Value) + ")";
                    else
                    {
                        var t = TimerCall.Match(cl.Value);   // setTimeout(() => refresh(), 3000): the callback holds brackets
                        if (t.Success) title = t.Groups["call"].Value + "(..., " + t.Groups["ms"].Value + ")";
                    }
                    if (title == null) continue;
                    var loc = Loc(f, cl.Key, "wait");
                    yield return new Finding
                    {
                        RuleId = "GNT01",
                        Kind = Kind.Timing,
                        Title = title,
                        GroupKey = loc.File + "|" + cl.Value.Trim(),
                        Locations = new List<CodeLocation> { loc },
                        Degree = 1,
                        FixedDistance = Distance.SameProject,
                        Cause = "This waits a fixed time, assuming other work will be done by then.",
                        Suggestion = "Wait for the real event (a promise, future, channel or condition with a timeout). Keep any retry timings in one policy.",
                        ChangeProbe = "If the other side gets slower, does this break? Is that likely?",
                    };
                }
        }

        // ---------- Globals ----------

        static IEnumerable<Finding> Globals(IList<TextFile> files)
        {
            var groups = new Dictionary<string, List<CodeLocation>>();
            foreach (var f in files)
                foreach (var cl in CodeLines(f))
                {
                    var pg = PyGlobal.Match(cl.Value);
                    if (pg.Success)
                        foreach (var n in pg.Groups["names"].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                            Add(groups, f.Project + "|" + n, Loc(f, cl.Key, "writes global"));
                    foreach (Match rg in RubyGlobal.Matches(cl.Value))
                        Add(groups, f.Project + "|$" + rg.Groups["name"].Value, Loc(f, cl.Key, "writes global"));
                }
            foreach (var kv in groups.Where(g => g.Value.Count >= 2))
            {
                var name = kv.Key.Split('|')[1];
                yield return new Finding
                {
                    RuleId = "GNI01",
                    Kind = Kind.Identity,
                    Title = "Global " + name + " changed in " + kv.Value.Count + " places",
                    GroupKey = kv.Key,
                    Locations = kv.Value,
                    Degree = kv.Value.Count,
                    Cause = "The global " + name + " is changed " + Text.Spread(kv.Value) + ". These places all depend on the same shared value and on each other's changes.",
                    Suggestion = "Give the state one owner (an object passed to whoever needs it), or return new values instead of changing a global.",
                    ChangeProbe = "If you need two of these, or run things in parallel, every place must change. Is that plausible?",
                };
            }
        }

        static void Add(Dictionary<string, List<CodeLocation>> g, string key, CodeLocation l)
        {
            List<CodeLocation> list;
            if (!g.TryGetValue(key, out list)) { list = new List<CodeLocation>(); g[key] = list; }
            list.Add(l);
        }
    }
}
