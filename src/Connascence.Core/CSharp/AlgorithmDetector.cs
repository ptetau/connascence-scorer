using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connascence.CSharp
{
    /// <summary>
    /// CNA01: the same logic copied into several members.
    /// CNA02: the same format string or regex written out in several places (writer and reader must agree).
    /// CNA03: the same hashing / crypto algorithm used independently in several places.
    /// </summary>
    public sealed class AlgorithmDetector : ICSharpDetector
    {
        static readonly ImmutableHashSet<string> FormatMethods = ImmutableHashSet.Create("ToString", "ParseExact", "TryParseExact");
        static readonly ImmutableHashSet<string> RegexMethods = ImmutableHashSet.Create("IsMatch", "Match", "Matches", "Replace", "Split");
        static readonly string[] CryptoTypes =
        {
            "MD5", "SHA1", "SHA256", "SHA384", "SHA512", "HMACMD5", "HMACSHA1", "HMACSHA256", "HMACSHA384", "HMACSHA512",
            "Aes", "AesGcm", "TripleDES", "RSA", "Rfc2898DeriveBytes", "ECDsa",
        };

        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return DuplicateBodies(ctx).Concat(SharedFormats(ctx)).Concat(Crypto(ctx));
        }

        sealed record Body
        {
            public required BlockSyntax Block { get; init; }
            public required IReadOnlyList<StatementSyntax> Statements { get; init; }
            public required IReadOnlyList<string> Keys { get; init; }   // one normalised key per top-level statement
            public required int[] Weight { get; init; }                 // statements inside each top-level statement, blocks not counted
        }

        sealed record Occurrence
        {
            public required Body Body { get; init; }
            public int Start { get; init; }
            public int Length { get; init; }
        }

        /// <summary>
        /// Finds the same run of statements in two or more members. Local and parameter names are normalised,
        /// so a copy with renamed variables still matches, and the copy may be part of a larger method
        /// (copied, then tweaked at the end). Whole identical bodies are the special case where the run is everything.
        /// </summary>
        IEnumerable<Finding> DuplicateBodies(CSharpContext ctx)
        {
            var min = ctx.Settings.MinDuplicateStatements;
            var bodies = new List<Body>();
            foreach (var root in ctx.Roots)
            {
                foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
                {
                    // Only whole bodies of members, local functions and lambdas: avoids reporting every nested block twice.
                    var p = block.Parent;
                    if (!(p is BaseMethodDeclarationSyntax || p is AccessorDeclarationSyntax || p is LocalFunctionStatementSyntax ||
                          p is AnonymousFunctionExpressionSyntax)) continue;
                    var total = block.DescendantNodes().OfType<StatementSyntax>().Count(st => !(st is BlockSyntax));
                    if (total < min) continue;
                    var locals = LocalNames(block);
                    var stmts = block.Statements.ToList();
                    bodies.Add(new Body
                    {
                        Block = block,
                        Statements = stmts,
                        Keys = stmts.Select(st => Normalise(st, locals)).ToList(),
                        Weight = stmts.Select(st => st.DescendantNodesAndSelf().OfType<StatementSyntax>().Count(x => !(x is BlockSyntax))).ToArray(),
                    });
                }
            }

            // Candidate pairs: bodies that share a run of three statements (or a whole short body). Very common runs are boilerplate.
            const int gram = 3;
            var index = new Dictionary<string, List<int>>();
            for (int b = 0; b < bodies.Count; b++)
            {
                var keys = bodies[b].Keys;
                var seen = new HashSet<string>();
                for (int i = 0; i + Math.Min(gram, keys.Count) <= keys.Count; i++)
                {
                    var g = string.Join("\n", keys.Skip(i).Take(Math.Min(gram, keys.Count)));
                    if (!seen.Add(g)) continue;
                    if (!index.TryGetValue(g, out var list)) { list = new List<int>(); index[g] = list; }
                    list.Add(b);
                }
            }
            var pairs = new HashSet<(int, int)>();
            foreach (var list in index.Values)
            {
                if (list.Count < 2 || list.Count > 50) continue;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                        pairs.Add((list[i], list[j]));
            }

            // For each pair, the longest common run of statements, kept if it holds enough statements.
            var groups = new Dictionary<string, List<Occurrence>>();
            foreach (var (a, b) in pairs.OrderBy(x => x.Item1).ThenBy(x => x.Item2))
            {
                if (Overlaps(bodies[a].Block, bodies[b].Block)) continue;   // a lambda inside the method it is compared with
                int ai, bi, len;
                LongestRun(bodies[a].Keys, bodies[b].Keys, out ai, out bi, out len);
                if (len == 0) continue;
                // Count each distinct statement once, so a run of look-alike guard clauses (if ($ == null) throw ...) isn't a "copy".
                var weight = 0;
                var counted = new HashSet<string>();
                for (int k = 0; k < len; k++) if (counted.Add(bodies[a].Keys[ai + k])) weight += bodies[a].Weight[ai + k];
                if (weight < min) continue;
                var key = string.Join("\n", bodies[a].Keys.Skip(ai).Take(len));
                if (!groups.TryGetValue(key, out var occ)) { occ = new List<Occurrence>(); groups[key] = occ; }
                if (!occ.Any(o => o.Body == bodies[a])) occ.Add(new Occurrence { Body = bodies[a], Start = ai, Length = len });
                if (!occ.Any(o => o.Body == bodies[b])) occ.Add(new Occurrence { Body = bodies[b], Start = bi, Length = len });
            }

            foreach (var kv in groups)
            {
                var occ = kv.Value.OrderBy(o => o.Body.Block.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(o => o.Body.Block.SpanStart).ToList();
                bool whole = occ.All(o => o.Start == 0 && o.Length == o.Body.Statements.Count);
                var locs = new List<CodeLocation>();
                foreach (var o in occ)
                {
                    CodeLocation loc;
                    int endLine;
                    var parent = o.Body.Block.Parent;
                    if (o.Start == 0 && o.Length == o.Body.Statements.Count)
                    {
                        loc = ctx.Locate(parent is BaseMethodDeclarationSyntax || parent is LocalFunctionStatementSyntax ? parent.GetFirstToken() : o.Body.Block.GetFirstToken(), "copy");
                        endLine = o.Body.Block.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                    }
                    else
                    {
                        loc = ctx.Locate(o.Body.Statements[o.Start], "copy");
                        endLine = o.Body.Statements[o.Start + o.Length - 1].GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                    }
                    loc.EndLine = endLine;
                    locs.Add(loc);
                }
                var first = occ[0];
                var n = 0;
                for (int k = 0; k < first.Length; k++) n += first.Body.Weight[first.Start + k];
                var renamed = occ.Select(o => string.Join("\n", o.Body.Statements.Skip(o.Start).Take(o.Length).Select(st => Normalise(st, null)))).Distinct().Count() > 1;
                yield return new Finding
                {
                    RuleId = "CNA01",
                    Kind = Kind.Algorithm,
                    Title = "Same logic copied " + occ.Count + " times",
                    GroupKey = Text.StableHash(kv.Key),
                    Locations = locs,
                    Degree = occ.Count,
                    Cause = (whole
                                ? "The same " + n + "-statement body appears "
                                : "The same run of " + n + " statements appears ") + Text.Spread(locs) + " (" + Text.Members(locs) + ")" +
                            (whole ? "" : ", inside methods that otherwise differ") +
                            (renamed ? ", with the variables renamed" : "") +
                            ". If the rule it encodes changes, every copy must change the same way.",
                    Suggestion = whole
                        ? "Move the logic into one method, owned by the concept it belongs to, and call it from each place."
                        : "Extract the shared statements into one method, owned by the concept they belong to, and call it from each place. Keep the parts that differ where they are.",
                    ChangeProbe = "If this rule changes (a bug fix, a new case, a policy tweak), will every copy be found and fixed? Is the rule likely to change?",
                };
            }
        }

        static bool Overlaps(SyntaxNode a, SyntaxNode b) => a.SyntaxTree == b.SyntaxTree && a.Span.IntersectsWith(b.Span);

        /// <summary>Longest run of equal keys shared by two lists (classic dynamic programming; bodies are short).</summary>
        static void LongestRun(IReadOnlyList<string> a, IReadOnlyList<string> b, out int aStart, out int bStart, out int length)
        {
            aStart = bStart = length = 0;
            var prev = new int[b.Count + 1];
            for (int i = 1; i <= a.Count; i++)
            {
                var cur = new int[b.Count + 1];
                for (int j = 1; j <= b.Count; j++)
                {
                    if (a[i - 1] != b[j - 1]) continue;
                    cur[j] = prev[j - 1] + 1;
                    if (cur[j] > length) { length = cur[j]; aStart = i - length; bStart = j - length; }
                }
                prev = cur;
            }
        }

        /// <summary>Names of parameters and locals declared in or for this body. These are renamed freely when code is copied.</summary>
        static ImmutableHashSet<string> LocalNames(BlockSyntax block)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            ParameterListSyntax? pars = null;
            switch (block.Parent)
            {
                case BaseMethodDeclarationSyntax m: pars = m.ParameterList; break;
                case LocalFunctionStatementSyntax lf: pars = lf.ParameterList; break;
                case ParenthesizedLambdaExpressionSyntax pl: pars = pl.ParameterList; break;
                case AnonymousMethodExpressionSyntax am: pars = am.ParameterList; break;
                case SimpleLambdaExpressionSyntax sl: names.Add(sl.Parameter.Identifier.Text); break;
                case AccessorDeclarationSyntax _: names.Add("value"); break;
            }
            if (pars != null) foreach (var p in pars.Parameters) names.Add(p.Identifier.Text);
            foreach (var n in block.DescendantNodes())
            {
                switch (n)
                {
                    case VariableDeclaratorSyntax v: names.Add(v.Identifier.Text); break;
                    case ForEachStatementSyntax f: names.Add(f.Identifier.Text); break;
                    case SingleVariableDesignationSyntax d: names.Add(d.Identifier.Text); break;
                    case CatchDeclarationSyntax c when c.Identifier.Text.Length > 0: names.Add(c.Identifier.Text); break;
                    case ParameterSyntax p: names.Add(p.Identifier.Text); break;
                }
            }
            return names.ToImmutableHashSet(StringComparer.Ordinal);
        }

        /// <summary>Token text of a statement, with local and parameter names replaced by a placeholder (when locals is given).</summary>
        static string Normalise(SyntaxNode node, ImmutableHashSet<string>? locals)
        {
            var sb = new StringBuilder();
            foreach (var t in node.DescendantTokens())
            {
                if (locals != null && t.IsKind(SyntaxKind.IdentifierToken) && locals.Contains(t.Text) && !IsMemberName(t))
                    sb.Append('$');
                else
                    sb.Append(t.Text);
                sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>True for the Name in x.Name (a member, not a local that happens to share its name).</summary>
        static bool IsMemberName(SyntaxToken t)
        {
            var id = t.Parent as SimpleNameSyntax;
            if (id == null) return false;
            var ma = id.Parent as MemberAccessExpressionSyntax;
            return (ma != null && ma.Name == id) || id.Parent is MemberBindingExpressionSyntax || id.Parent is NameColonSyntax ||
                   (id.Parent is AssignmentExpressionSyntax asg && asg.Left == id && asg.Parent is InitializerExpressionSyntax);
        }

        IEnumerable<Finding> SharedFormats(CSharpContext ctx)
        {
            var groups = new Dictionary<string, List<LiteralExpressionSyntax>>();
            foreach (var root in ctx.Roots)
            {
                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = (inv.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
                    if (name == null) continue;
                    var receiver = ((MemberAccessExpressionSyntax)inv.Expression).Expression.ToString();
                    string category;
                    if (FormatMethods.Contains(name)) category = "format";
                    else if (RegexMethods.Contains(name) && receiver == "Regex") category = "pattern";
                    else continue;
                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        if (!(arg.Expression is LiteralExpressionSyntax lit) || !lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                        // For Regex statics the input comes first; only the later literal args are patterns.
                        if (category == "pattern" && arg == inv.ArgumentList.Arguments[0]) continue;
                        if (lit.Token.ValueText.Length < 2) continue;
                        Add(groups, category + "|" + lit.Token.ValueText, lit);
                    }
                }
                foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    if (oc.Type.ToString() != "Regex" || oc.ArgumentList == null || oc.ArgumentList.Arguments.Count == 0) continue;
                    if (oc.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                        Add(groups, "pattern|" + lit.Token.ValueText, lit);
                }
            }

            foreach (var kv in groups.Where(g => g.Value.Count >= 2))
            {
                var lits = kv.Value;
                var locs = lits.Select(l => ctx.Locate(l, "use")).ToList();
                if (locs.Select(l => l.Anchor).Distinct().Count() < 2) continue;
                var isPattern = kv.Key.StartsWith("pattern|", StringComparison.Ordinal);
                var what = (isPattern ? "regex pattern " : "format ") + "\"" + Text.Quote(lits[0].Token.ValueText) + "\"";
                yield return new Finding
                {
                    RuleId = "CNA02",
                    Kind = Kind.Algorithm,
                    Title = "Repeated " + what,
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = locs.Count,
                    Cause = "The " + what + " is written out " + Text.Spread(locs) +
                            ". Code that writes and code that reads this format must agree on it exactly.",
                    Suggestion = isPattern
                        ? "Keep the pattern in one place (a static Regex field or a [GeneratedRegex] method) and use it from every caller."
                        : "Keep the format in one place (a constant, or a small formatter/parser class) and use it from both the writing and reading side.",
                    ChangeProbe = "If the format changes (a new locale, an extra field, a stricter pattern), will every copy change at once? A missed one fails at runtime, not compile time.",
                };
            }
        }

        static void Add<T>(Dictionary<string, List<T>> groups, string key, T item)
        {
            if (!groups.TryGetValue(key, out var list)) { list = new List<T>(); groups[key] = list; }
            list.Add(item);
        }

        IEnumerable<Finding> Crypto(CSharpContext ctx)
        {
            var known = new HashSet<string>(CryptoTypes);
            var groups = new Dictionary<string, List<SyntaxNode>>();
            foreach (var root in ctx.Roots)
            {
                foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var name = id.Identifier.Text;
                    if (!known.Contains(name)) continue;
                    // Must be used as a type: SHA256.Create(), new HMACSHA256(...), SHA256.HashData(...)
                    var p = id.Parent;
                    bool asType = (p is MemberAccessExpressionSyntax ma && ma.Expression == id) || p is ObjectCreationExpressionSyntax;
                    if (!asType) continue;
                    if (id.Ancestors().OfType<UsingDirectiveSyntax>().Any()) continue;
                    Add(groups, name, id);
                }
            }

            foreach (var kv in groups)
            {
                var locs = kv.Value.Select(n => ctx.Locate(n, "use")).GroupBy(l => l.Anchor).Select(g => g.First()).ToList();
                if (locs.Select(l => l.Type).Distinct().Count() < 2) continue;
                yield return new Finding
                {
                    RuleId = "CNA03",
                    Kind = Kind.Algorithm,
                    Title = kv.Key + " used independently in " + locs.Count + " places",
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = locs.Count,
                    Cause = kv.Key + " is set up separately " + Text.Spread(locs) + " (" + Text.Members(locs) +
                            "). These places must agree on the algorithm, key, text encoding and output format, or results silently stop matching.",
                    Suggestion = "Put the " + kv.Key + " work behind one small class (for example a hasher or signer) that fixes the algorithm, encoding and format, and use it everywhere.",
                    ChangeProbe = "If the algorithm or encoding needs to change (a security upgrade, a new key, a partner's spec), can it change in one place?",
                };
            }
        }
    }
}
