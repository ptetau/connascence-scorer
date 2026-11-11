using System.Collections.Generic;
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
        static readonly HashSet<string> FormatMethods = new HashSet<string> { "ToString", "ParseExact", "TryParseExact" };
        static readonly HashSet<string> RegexMethods = new HashSet<string> { "IsMatch", "Match", "Matches", "Replace", "Split" };
        static readonly string[] CryptoTypes =
        {
            "MD5", "SHA1", "SHA256", "SHA384", "SHA512", "HMACMD5", "HMACSHA1", "HMACSHA256", "HMACSHA384", "HMACSHA512",
            "Aes", "AesGcm", "TripleDES", "RSA", "Rfc2898DeriveBytes", "ECDsa",
        };

        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return DuplicateBodies(ctx).Concat(SharedFormats(ctx)).Concat(Crypto(ctx));
        }

        IEnumerable<Finding> DuplicateBodies(CSharpContext ctx)
        {
            var min = ctx.Settings.MinDuplicateStatements;
            var groups = new Dictionary<string, List<SyntaxNode>>();
            foreach (var root in ctx.Roots)
            {
                foreach (var body in root.DescendantNodes().OfType<BlockSyntax>())
                {
                    // Only whole bodies of members, local functions and lambdas: avoids reporting every nested block twice.
                    var p = body.Parent;
                    if (!(p is BaseMethodDeclarationSyntax || p is AccessorDeclarationSyntax || p is LocalFunctionStatementSyntax ||
                          p is AnonymousFunctionExpressionSyntax)) continue;
                    var statements = body.DescendantNodes().OfType<StatementSyntax>().Count(st => !(st is BlockSyntax));
                    if (statements < min) continue;
                    var key = Normalise(body);
                    List<SyntaxNode> list;
                    if (!groups.TryGetValue(key, out list)) { list = new List<SyntaxNode>(); groups[key] = list; }
                    list.Add(body);
                }
            }

            foreach (var kv in groups.Where(g => g.Value.Count >= 2))
            {
                var bodies = kv.Value;
                var locs = bodies.Select(b => ctx.Locate(b.Parent is BaseMethodDeclarationSyntax || b.Parent is LocalFunctionStatementSyntax ? b.Parent.GetFirstToken() : b.GetFirstToken(), "copy")).ToList();
                for (int i = 0; i < bodies.Count; i++) locs[i].EndLine = bodies[i].GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                var n = bodies[0].DescendantNodes().OfType<StatementSyntax>().Count(st => !(st is BlockSyntax));
                yield return new Finding
                {
                    RuleId = "CNA01",
                    Kind = Kind.Algorithm,
                    Title = "Same logic copied " + bodies.Count + " times",
                    GroupKey = Text.StableHash(kv.Key),
                    Locations = locs,
                    Degree = bodies.Count,
                    Cause = "The same " + n + "-statement body appears " + Text.Spread(locs) + " (" + Text.Members(locs) +
                            "). If the rule it encodes changes, every copy must change the same way.",
                    Suggestion = "Move the logic into one method, owned by the concept it belongs to, and call it from each place.",
                    ChangeProbe = "If this rule changes (a bug fix, a new case, a policy tweak), will every copy be found and fixed? Is the rule likely to change?",
                };
            }
        }

        static string Normalise(SyntaxNode node)
        {
            var sb = new StringBuilder();
            foreach (var t in node.DescendantTokens()) { sb.Append(t.Text); sb.Append(' '); }
            return sb.ToString();
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
                var isPattern = kv.Key.StartsWith("pattern|");
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
            List<T> list;
            if (!groups.TryGetValue(key, out list)) { list = new List<T>(); groups[key] = list; }
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
