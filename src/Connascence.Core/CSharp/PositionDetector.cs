using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connascence.CSharp
{
    /// <summary>
    /// CNP01: long parameter lists with look-alike types, called by position.
    /// CNP02: tuples that callers must unpack by position.
    /// CNP03: picking fields out of a split string by index.
    /// </summary>
    public sealed class PositionDetector : ICSharpDetector
    {
        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return Parameters(ctx).Concat(Tuples(ctx)).Concat(Indexes(ctx));
        }

        IEnumerable<Finding> Parameters(CSharpContext ctx)
        {
            var s = ctx.Settings;
            foreach (var root in ctx.Roots)
            {
                foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var ps = decl.ParameterList?.Parameters;
                    if (ps == null || ps.Value.Count < s.MinParametersForPosition) continue;
                    var sameType = ps.Value.Where(p => p.Type != null)
                        .GroupBy(p => p.Type.ToString()).OrderByDescending(g => g.Count()).FirstOrDefault();
                    if (sameType == null || sameType.Count() < s.MinSameTypeParameters) continue;

                    var symbol = ctx.Model(decl.SyntaxTree).GetDeclaredSymbol(decl) as IMethodSymbol;
                    if (symbol == null) continue;
                    var positional = ctx.CallsTo(symbol)
                        .Where(c => c.Args.Arguments.Count >= 2 && c.Args.Arguments.Skip(1).Any(a => a.NameColon == null))
                        .ToList();
                    if (positional.Count == 0) continue;

                    var name = decl is ConstructorDeclarationSyntax cd ? "new " + cd.Identifier.Text : symbol.Name;
                    var locs = new List<CodeLocation> { ctx.Locate(decl.ParameterList, "declaration") };
                    locs.AddRange(positional.Select(c => ctx.Locate(c.Node, "call site")));
                    var typeName = sameType.Key;
                    var sameNames = string.Join(", ", sameType.Select(p => p.Identifier.Text).ToArray());

                    yield return new Finding
                    {
                        RuleId = "CNP01",
                        Kind = Kind.Position,
                        Title = name + " takes " + ps.Value.Count + " parameters by position",
                        GroupKey = symbol.ToDisplayString(),
                        Locations = locs,
                        Degree = positional.Count,
                        Cause = name + " takes " + ps.Value.Count + " parameters, " + sameType.Count() + " of them " + typeName + " (" + sameNames + "). " +
                                Text.CountVerb(positional.Count, "caller", "passes", "pass") + " them by position, so swapping two " + typeName +
                                " values still compiles and silently gives the wrong result.",
                        Suggestion = "Group the related values into a small type (for example a record holding " + sameNames +
                                     "), or give each value its own type so mix-ups don't compile. Quick step: have callers use named arguments.",
                        ChangeProbe = "If a parameter is added, removed or reordered, " + Text.All(positional.Count, "positional caller") +
                                      " must change in step. Is that likely as this feature grows?",
                    };
                }
            }
        }

        IEnumerable<Finding> Tuples(CSharpContext ctx)
        {
            var s = ctx.Settings;
            foreach (var root in ctx.Roots)
            {
                foreach (var decl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    int elements;
                    bool unnamed;
                    if (decl.ReturnType is TupleTypeSyntax tt)
                    {
                        elements = tt.Elements.Count;
                        unnamed = tt.Elements.Any(e => e.Identifier.IsKind(SyntaxKind.None));
                    }
                    else if (decl.ReturnType is GenericNameSyntax g && g.Identifier.Text == "Tuple")
                    {
                        elements = g.TypeArgumentList.Arguments.Count;
                        unnamed = true;
                    }
                    else continue;

                    if (elements < s.MinTupleElements && !unnamed) continue;
                    if (elements < 2) continue;
                    var symbol = ctx.Model(decl.SyntaxTree).GetDeclaredSymbol(decl);
                    if (symbol == null) continue;
                    var calls = ctx.CallsTo(symbol);
                    if (calls.Count == 0) continue;

                    var locs = new List<CodeLocation> { ctx.Locate(decl.ReturnType, "declaration") };
                    locs.AddRange(calls.Select(c => ctx.Locate(c.Node, "call site")));
                    yield return new Finding
                    {
                        RuleId = "CNP02",
                        Kind = Kind.Position,
                        Title = decl.Identifier.Text + " returns a " + elements + "-part tuple",
                        GroupKey = symbol.ToDisplayString(),
                        Locations = locs,
                        Degree = calls.Count,
                        Cause = decl.Identifier.Text + " returns " + elements + " values in a tuple" + (unnamed ? " with unnamed parts" : "") + ". " +
                                Text.Count(calls.Count, "caller") + " must unpack them in the right order.",
                        Suggestion = "Return a small named type (a record) instead, so callers read values by name, not by position.",
                        ChangeProbe = "If a value is added to or removed from the result, every caller that unpacks it must change. Is the result likely to grow?",
                    };
                }
            }
        }

        IEnumerable<Finding> Indexes(CSharpContext ctx)
        {
            foreach (var root in ctx.Roots)
            {
                var groups = new Dictionary<string, List<ElementAccessExpressionSyntax>>();
                foreach (var ea in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
                {
                    if (ea.ArgumentList.Arguments.Count != 1) continue;
                    if (!ea.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.NumericLiteralExpression)) continue;
                    if (ea.Parent is AssignmentExpressionSyntax asg && asg.Left == ea) continue; // writing into an array is fine

                    bool fromSplit = IsSplit(ea.Expression);
                    if (!fromSplit)
                    {
                        var t = ctx.TypeOf(ea.Expression) as IArrayTypeSymbol;
                        if (t == null || t.ElementType.SpecialType != SpecialType.System_String) continue;
                        if (!IsLocalFromSplit(ea)) continue;
                    }
                    var key = CSharpContext.TypeNameOf(ea) + "." + CSharpContext.MemberNameOf(ea) + "|" + ea.Expression.ToString();
                    List<ElementAccessExpressionSyntax> list;
                    if (!groups.TryGetValue(key, out list)) { list = new List<ElementAccessExpressionSyntax>(); groups[key] = list; }
                    list.Add(ea);
                }

                foreach (var kv in groups)
                {
                    var uses = kv.Value;
                    var indices = uses.Select(u => u.ArgumentList.Arguments[0].ToString()).Distinct().ToList();
                    var locs = uses.Select(u => ctx.Locate(u, "use")).ToList();
                    var expr = uses[0].Expression.ToString();
                    if (expr.Length > 40) expr = expr.Substring(0, 37) + "...";
                    yield return new Finding
                    {
                        RuleId = "CNP03",
                        Kind = Kind.Position,
                        Title = "Fields read by index from " + expr,
                        GroupKey = kv.Key,
                        Locations = locs,
                        Degree = indices.Count,
                        FixedDistance = Distance.SameProject, // the format is defined by whoever wrote the string
                        Cause = "This code reads " + Text.Count(indices.Count, "field") + " out of a split string by position (" +
                                string.Join(", ", indices.Select(i => "[" + i + "]").ToArray()) +
                                "). It depends on the exact field order chosen by whatever produced the string.",
                        Suggestion = "Parse the string once, in one place next to the code that defines the format, into a small type with named properties. Or use a regex with named groups.",
                        ChangeProbe = "If a field is added to or removed from the format, every index here shifts. Does the format come from somewhere that might change?",
                    };
                }
            }
        }

        static bool IsSplit(ExpressionSyntax e) =>
            e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Split";

        static bool IsLocalFromSplit(ElementAccessExpressionSyntax ea)
        {
            if (!(ea.Expression is IdentifierNameSyntax id)) return false;
            var member = ea.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member == null) return false;
            return member.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Any(v => v.Identifier.Text == id.Identifier.Text && v.Initializer != null && IsSplit(v.Initializer.Value));
        }
    }
}
