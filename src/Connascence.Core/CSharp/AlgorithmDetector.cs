using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    public sealed record AlgorithmDetector : ICSharpDetector
    {
        static readonly ImmutableHashSet<string> FormatMethods = ImmutableHashSet.Create("ToString", "ParseExact", "TryParseExact");
        static readonly ImmutableHashSet<string> RegexMethods = ImmutableHashSet.Create("IsMatch", "Match", "Matches", "Replace", "Split");
        static readonly ImmutableHashSet<string> CryptoTypes = ImmutableHashSet.Create(
            "MD5", "SHA1", "SHA256", "SHA384", "SHA512", "HMACMD5", "HMACSHA1", "HMACSHA256", "HMACSHA384", "HMACSHA512",
            "Aes", "AesGcm", "TripleDES", "RSA", "Rfc2898DeriveBytes", "ECDsa");

        public IEnumerable<Finding> Detect(CSharpContext ctx) =>
            DuplicateBodies(ctx).Concat(SharedFormats(ctx)).Concat(Crypto(ctx));

        sealed record Body
        {
            public required BlockSyntax Block { get; init; }
            public required ImmutableArray<StatementSyntax> Statements { get; init; }
            public required ImmutableArray<string> Keys { get; init; }   // one normalised key per top-level statement
            public required ImmutableArray<int> Weight { get; init; }    // statements inside each top-level statement, blocks not counted
        }

        sealed record Occurrence
        {
            public required Body Body { get; init; }
            public int Start { get; init; }
            public int Length { get; init; }
        }

        /// <summary>The longest run of equal keys two lists share: where it starts in each, and how long it is.</summary>
        readonly record struct Run(int AStart, int BStart, int Length);

        /// <summary>
        /// Finds the same run of statements in two or more members. Local and parameter names are normalised,
        /// so a copy with renamed variables still matches, and the copy may be part of a larger method
        /// (copied, then tweaked at the end). Whole identical bodies are the special case where the run is everything.
        /// </summary>
        static IEnumerable<Finding> DuplicateBodies(CSharpContext ctx)
        {
            var min = ctx.Settings.MinDuplicateStatements;
            var bodies = ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<BlockSyntax>())
                .Where(block => IsWholeBody(block) && StatementCount(block) >= min)
                .Select(BodyOf)
                .ToImmutableArray();

            // For each candidate pair, the longest common run of statements, kept if it holds enough statements.
            // Each group lists the bodies that share a run, in the order the pairs found them.
            return CandidatePairs(bodies)
                .Select(pair => (A: bodies[pair.A], B: bodies[pair.B]))
                .Where(pair => !Overlaps(pair.A.Block, pair.B.Block))   // a lambda inside the method it is compared with
                .Select(pair => (pair.A, pair.B, Run: LongestRun(pair.A.Keys, pair.B.Keys)))
                .Where(x => x.Run.Length > 0 && DistinctWeight(x.A, x.Run) >= min)
                .SelectMany(x => new[]
                {
                    (Key: string.Join("\n", x.A.Keys.Skip(x.Run.AStart).Take(x.Run.Length)), Occurrence: new Occurrence { Body = x.A, Start = x.Run.AStart, Length = x.Run.Length }),
                    (Key: string.Join("\n", x.A.Keys.Skip(x.Run.AStart).Take(x.Run.Length)), Occurrence: new Occurrence { Body = x.B, Start = x.Run.BStart, Length = x.Run.Length }),
                })
                .GroupBy(x => x.Key, x => x.Occurrence)
                .Select(g => Copied(ctx, g.Key, g.GroupBy(o => o.Body).Select(same => same.First()).ToImmutableArray()));
        }

        /// <summary>Only whole bodies of members, local functions and lambdas: avoids reporting every nested block twice.</summary>
        static bool IsWholeBody(BlockSyntax block) =>
            block.Parent is BaseMethodDeclarationSyntax || block.Parent is AccessorDeclarationSyntax ||
            block.Parent is LocalFunctionStatementSyntax || block.Parent is AnonymousFunctionExpressionSyntax;

        static int StatementCount(SyntaxNode node) =>
            node.DescendantNodesAndSelf().OfType<StatementSyntax>().Count(st => !(st is BlockSyntax));

        static Body BodyOf(BlockSyntax block)
        {
            var locals = LocalNames(block);
            var statements = block.Statements.ToImmutableArray();
            return new Body
            {
                Block = block,
                Statements = statements,
                Keys = statements.Select(st => Normalise(st, locals)).ToImmutableArray(),
                Weight = statements.Select(StatementCount).ToImmutableArray(),
            };
        }

        /// <summary>
        /// Pairs of bodies (by index, each pair once, in order) that share a run of three statements, or a whole short body.
        /// Runs shared by more than 50 bodies are boilerplate and don't count.
        /// </summary>
        static IEnumerable<(int A, int B)> CandidatePairs(ImmutableArray<Body> bodies)
        {
            const int gram = 3;
            return bodies
                .SelectMany((body, b) => Grams(body.Keys, Math.Min(gram, body.Keys.Length)).Distinct().Select(g => (Gram: g, Body: b)))
                .GroupBy(x => x.Gram, x => x.Body)
                .Select(g => g.ToImmutableArray())
                .Where(list => list.Length >= 2 && list.Length <= 50)
                .SelectMany(list => list.SelectMany((a, i) => list.Skip(i + 1).Select(b => (A: a, B: b))))
                .Distinct()
                .OrderBy(x => x.A).ThenBy(x => x.B);
        }

        /// <summary>Every run of n consecutive keys, joined into one string.</summary>
        static IEnumerable<string> Grams(ImmutableArray<string> keys, int n) =>
            Enumerable.Range(0, keys.Length - n + 1).Select(i => string.Join("\n", keys.Skip(i).Take(n)));

        /// <summary>
        /// Statements in the run, counting each distinct statement once, so a run of look-alike guard clauses
        /// (if ($ == null) throw ...) isn't a "copy".
        /// </summary>
        static int DistinctWeight(Body body, Run run) =>
            Enumerable.Range(run.AStart, run.Length).GroupBy(k => body.Keys[k]).Sum(g => body.Weight[g.First()]);

        static Finding Copied(CSharpContext ctx, string key, ImmutableArray<Occurrence> found)
        {
            var occ = found.OrderBy(o => o.Body.Block.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(o => o.Body.Block.SpanStart).ToImmutableArray();
            bool whole = occ.All(o => o.Start == 0 && o.Length == o.Body.Statements.Length);
            var locs = occ.Select(o => CopyLocation(ctx, o)).ToImmutableArray();
            var first = occ[0];
            var n = first.Body.Weight.Skip(first.Start).Take(first.Length).Sum();
            var renamed = occ.Select(o => string.Join("\n", o.Body.Statements.Skip(o.Start).Take(o.Length).Select(st => Normalise(st, null)))).Distinct().Count() > 1;
            return new Finding
            {
                RuleId = "CNA01",
                Kind = Kind.Algorithm,
                Title = "Same logic copied " + occ.Length + " times",
                GroupKey = Text.StableHash(key),
                Locations = locs,
                Degree = occ.Length,
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

        /// <summary>Where a copy starts and ends: the member's declaration for a whole body, otherwise the copied statements.</summary>
        static CodeLocation CopyLocation(CSharpContext ctx, Occurrence o)
        {
            var parent = o.Body.Block.Parent;
            if (o.Start == 0 && o.Length == o.Body.Statements.Length)
            {
                var start = parent is BaseMethodDeclarationSyntax || parent is LocalFunctionStatementSyntax ? parent.GetFirstToken() : o.Body.Block.GetFirstToken();
                return ctx.Locate(start, "copy") with { EndLine = o.Body.Block.GetLocation().GetLineSpan().EndLinePosition.Line + 1 };
            }
            return ctx.Locate(o.Body.Statements[o.Start], "copy") with
            {
                EndLine = o.Body.Statements[o.Start + o.Length - 1].GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            };
        }

        static bool Overlaps(SyntaxNode a, SyntaxNode b) => a.SyntaxTree == b.SyntaxTree && a.Span.IntersectsWith(b.Span);

        /// <summary>
        /// Longest run of equal keys shared by two lists (classic dynamic programming; bodies are short). Each row of the
        /// table holds, for each position in b, the length of the run ending there. The first longest run wins.
        /// </summary>
        static Run LongestRun(ImmutableArray<string> a, ImmutableArray<string> b) =>
            Enumerable.Range(1, a.Length).Aggregate(
                (Previous: Enumerable.Repeat(0, b.Length + 1).ToImmutableArray(), Best: new Run(0, 0, 0)),
                (state, i) =>
                {
                    var row = Enumerable.Range(0, b.Length + 1)
                        .Select(j => j > 0 && a[i - 1] == b[j - 1] ? state.Previous[j - 1] + 1 : 0)
                        .ToImmutableArray();
                    var best = Enumerable.Range(1, b.Length)
                        .Aggregate(state.Best, (top, j) => row[j] > top.Length ? new Run(i - row[j], j - row[j], row[j]) : top);
                    return (row, best);
                }).Best;

        /// <summary>Names of parameters and locals declared in or for this body. These are renamed freely when code is copied.</summary>
        static ImmutableHashSet<string> LocalNames(BlockSyntax block)
        {
            var parameters = block.Parent switch
            {
                BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.Select(p => p.Identifier.Text),
                LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters.Select(p => p.Identifier.Text),
                ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters.Select(p => p.Identifier.Text),
                AnonymousMethodExpressionSyntax am => am.ParameterList?.Parameters.Select(p => p.Identifier.Text) ?? Enumerable.Empty<string>(),
                SimpleLambdaExpressionSyntax sl => new[] { sl.Parameter.Identifier.Text },
                AccessorDeclarationSyntax _ => new[] { "value" },
                _ => Enumerable.Empty<string>(),
            };
            var declared = block.DescendantNodes().Select(n => n switch
            {
                VariableDeclaratorSyntax v => v.Identifier.Text,
                ForEachStatementSyntax f => f.Identifier.Text,
                SingleVariableDesignationSyntax d => d.Identifier.Text,
                CatchDeclarationSyntax c when c.Identifier.Text.Length > 0 => c.Identifier.Text,
                ParameterSyntax p => p.Identifier.Text,
                _ => null,
            }).OfType<string>();
            return parameters.Concat(declared).ToImmutableHashSet(StringComparer.Ordinal);
        }

        /// <summary>Token text of a statement, with local and parameter names replaced by a placeholder (when locals is given).</summary>
        static string Normalise(SyntaxNode node, ImmutableHashSet<string>? locals) =>
            string.Concat(node.DescendantTokens().Select(t =>
                (locals != null && t.IsKind(SyntaxKind.IdentifierToken) && locals.Contains(t.Text) && !IsMemberName(t) ? "$" : t.Text) + " "));

        /// <summary>True for the Name in x.Name (a member, not a local that happens to share its name).</summary>
        static bool IsMemberName(SyntaxToken t)
        {
            var id = t.Parent as SimpleNameSyntax;
            if (id == null) return false;
            var ma = id.Parent as MemberAccessExpressionSyntax;
            return (ma != null && ma.Name == id) || id.Parent is MemberBindingExpressionSyntax || id.Parent is NameColonSyntax ||
                   (id.Parent is AssignmentExpressionSyntax asg && asg.Left == id && asg.Parent is InitializerExpressionSyntax);
        }

        static IEnumerable<Finding> SharedFormats(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => FormatArguments(root).Concat(RegexConstructions(root)))
                .GroupBy(x => x.Key, x => x.Literal)
                .Select(g => g.ToImmutableArray() is var lits && lits.Length >= 2 ? SharedFormat(ctx, g.Key, lits) : null)
                .OfType<Finding>();

        /// <summary>Literal formats passed to ToString/ParseExact, and patterns passed to Regex statics, keyed by category and text.</summary>
        static IEnumerable<(string Key, LiteralExpressionSyntax Literal)> FormatArguments(SyntaxNode root) =>
            root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax)
                .SelectMany(inv =>
                {
                    var ma = (MemberAccessExpressionSyntax)inv.Expression;
                    var name = ma.Name.Identifier.Text;
                    var category = FormatMethods.Contains(name) ? "format"
                        : RegexMethods.Contains(name) && ma.Expression.ToString() == "Regex" ? "pattern"
                        : null;
                    if (category == null) return Enumerable.Empty<(string, LiteralExpressionSyntax)>();
                    return inv.ArgumentList.Arguments
                        // For Regex statics the input comes first; only the later literal args are patterns.
                        .Where(arg => !(category == "pattern" && arg == inv.ArgumentList.Arguments[0]))
                        .Select(arg => arg.Expression as LiteralExpressionSyntax)
                        .OfType<LiteralExpressionSyntax>()
                        .Where(lit => lit.IsKind(SyntaxKind.StringLiteralExpression) && lit.Token.ValueText.Length >= 2)
                        .Select(lit => (category + "|" + lit.Token.ValueText, lit));
                });

        /// <summary>Literal patterns passed to new Regex(...).</summary>
        static IEnumerable<(string Key, LiteralExpressionSyntax Literal)> RegexConstructions(SyntaxNode root) =>
            root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(oc => oc.Type.ToString() == "Regex" && oc.ArgumentList != null && oc.ArgumentList.Arguments.Count > 0)
                .Select(oc => oc.ArgumentList!.Arguments[0].Expression as LiteralExpressionSyntax)
                .OfType<LiteralExpressionSyntax>()
                .Where(lit => lit.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(lit => ("pattern|" + lit.Token.ValueText, lit));

        static Finding? SharedFormat(CSharpContext ctx, string key, ImmutableArray<LiteralExpressionSyntax> lits)
        {
            var locs = lits.Select(l => ctx.Locate(l, "use")).ToImmutableArray();
            if (locs.Select(l => l.Anchor).Distinct().Count() < 2) return null;
            var isPattern = key.StartsWith("pattern|", StringComparison.Ordinal);
            var what = (isPattern ? "regex pattern " : "format ") + "\"" + Text.Quote(lits[0].Token.ValueText) + "\"";
            return new Finding
            {
                RuleId = "CNA02",
                Kind = Kind.Algorithm,
                Title = "Repeated " + what,
                GroupKey = key,
                Locations = locs,
                Degree = locs.Length,
                Cause = "The " + what + " is written out " + Text.Spread(locs) +
                        ". Code that writes and code that reads this format must agree on it exactly.",
                Suggestion = isPattern
                    ? "Keep the pattern in one place (a static Regex field or a [GeneratedRegex] method) and use it from every caller."
                    : "Keep the format in one place (a constant, or a small formatter/parser class) and use it from both the writing and reading side.",
                ChangeProbe = "If the format changes (a new locale, an extra field, a stricter pattern), will every copy change at once? A missed one fails at runtime, not compile time.",
            };
        }

        static IEnumerable<Finding> Crypto(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<IdentifierNameSyntax>())
                .Where(id => CryptoTypes.Contains(id.Identifier.Text))
                // Must be used as a type: SHA256.Create(), new HMACSHA256(...), SHA256.HashData(...)
                .Where(id => (id.Parent is MemberAccessExpressionSyntax ma && ma.Expression == id) || id.Parent is ObjectCreationExpressionSyntax)
                .Where(id => !id.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                .GroupBy(id => id.Identifier.Text)
                .Select(g => Crypto(ctx, g.Key, g.ToImmutableArray()))
                .OfType<Finding>();

        static Finding? Crypto(CSharpContext ctx, string algorithm, ImmutableArray<IdentifierNameSyntax> uses)
        {
            var locs = uses.Select(n => ctx.Locate(n, "use")).GroupBy(l => l.Anchor).Select(g => g.First()).ToImmutableArray();
            if (locs.Select(l => l.Type).Distinct().Count() < 2) return null;
            return new Finding
            {
                RuleId = "CNA03",
                Kind = Kind.Algorithm,
                Title = algorithm + " used independently in " + locs.Length + " places",
                GroupKey = algorithm,
                Locations = locs,
                Degree = locs.Length,
                Cause = algorithm + " is set up separately " + Text.Spread(locs) + " (" + Text.Members(locs) +
                        "). These places must agree on the algorithm, key, text encoding and output format, or results silently stop matching.",
                Suggestion = "Put the " + algorithm + " work behind one small class (for example a hasher or signer) that fixes the algorithm, encoding and format, and use it everywhere.",
                ChangeProbe = "If the algorithm or encoding needs to change (a security upgrade, a new key, a partner's spec), can it change in one place?",
            };
        }
    }
}
