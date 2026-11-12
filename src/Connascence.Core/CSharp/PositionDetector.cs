using System.Collections.Generic;
using System.Collections.Immutable;
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
    public sealed record PositionDetector : ICSharpDetector
    {
        public IEnumerable<Finding> Detect(CSharpContext ctx) =>
            Parameters(ctx).Concat(Tuples(ctx)).Concat(Indexes(ctx));

        static IEnumerable<Finding> Parameters(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                .Select(decl => Parameters(ctx, decl))
                .OfType<Finding>();

        static Finding? Parameters(CSharpContext ctx, BaseMethodDeclarationSyntax decl)
        {
            var s = ctx.Settings;
            var ps = decl.ParameterList?.Parameters;
            if (ps == null || ps.Value.Count < s.MinParametersForPosition) return null;
            var sameType = ps.Value.Where(p => p.Type != null)
                .GroupBy(p => p.Type!.ToString()).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (sameType == null || sameType.Count() < s.MinSameTypeParameters) return null;

            var symbol = ctx.Model(decl.SyntaxTree).GetDeclaredSymbol(decl) as IMethodSymbol;
            if (symbol == null) return null;
            var positional = ctx.CallsTo(symbol)
                .Where(c => c.Args.Arguments.Count >= 2 && c.Args.Arguments.Skip(1).Any(a => a.NameColon == null))
                .ToImmutableArray();
            if (positional.IsEmpty) return null;

            var name = decl is ConstructorDeclarationSyntax cd ? "new " + cd.Identifier.Text : symbol.Name;
            var locs = positional.Select(c => ctx.Locate(c.Node, "call site")).Prepend(ctx.Locate(decl.ParameterList!, "declaration")).ToImmutableArray();
            var typeName = sameType.Key;
            var sameNames = string.Join(", ", sameType.Select(p => p.Identifier.Text).ToArray());

            return new Finding
            {
                RuleId = "CNP01",
                Kind = Kind.Position,
                Title = name + " takes " + ps.Value.Count + " parameters by position",
                GroupKey = symbol.ToDisplayString(),
                Locations = locs,
                Degree = positional.Length,
                Cause = name + " takes " + ps.Value.Count + " parameters, " + sameType.Count() + " of them " + typeName + " (" + sameNames + "). " +
                        Text.CountVerb(positional.Length, "caller", "passes", "pass") + " them by position, so swapping two " + typeName +
                        " values still compiles and silently gives the wrong result.",
                Suggestion = "Group the related values into a small type (for example a record holding " + sameNames +
                             "), or give each value its own type so mix-ups don't compile. Quick step: have callers use named arguments.",
                ChangeProbe = "If a parameter is added, removed or reordered, " + Text.All(positional.Length, "positional caller") +
                              " must change in step. Is that likely as this feature grows?",
            };
        }

        static IEnumerable<Finding> Tuples(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                .Select(decl => Tuples(ctx, decl))
                .OfType<Finding>();

        static Finding? Tuples(CSharpContext ctx, MethodDeclarationSyntax decl)
        {
            var shape = decl.ReturnType switch
            {
                TupleTypeSyntax tt => (Elements: tt.Elements.Count, Unnamed: tt.Elements.Any(e => e.Identifier.IsKind(SyntaxKind.None))),
                GenericNameSyntax g when g.Identifier.Text == "Tuple" => (Elements: g.TypeArgumentList.Arguments.Count, Unnamed: true),
                _ => ((int Elements, bool Unnamed)?)null,
            };
            if (shape is not { } tuple) return null;
            var (elements, unnamed) = tuple;

            if (elements < ctx.Settings.MinTupleElements && !unnamed) return null;
            if (elements < 2) return null;
            var symbol = ctx.Model(decl.SyntaxTree).GetDeclaredSymbol(decl);
            if (symbol == null) return null;
            var calls = ctx.CallsTo(symbol);
            if (calls.Count == 0) return null;

            var locs = calls.Select(c => ctx.Locate(c.Node, "call site")).Prepend(ctx.Locate(decl.ReturnType, "declaration")).ToImmutableArray();
            return new Finding
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

        /// <summary>Reads like parts[2] from a split string, grouped per member and expression, one finding per group, per file.</summary>
        static IEnumerable<Finding> Indexes(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .Where(ea => ReadsSplitField(ctx, ea))
                .GroupBy(ea => CSharpContext.TypeNameOf(ea) + "." + CSharpContext.MemberNameOf(ea) + "|" + ea.Expression.ToString())
                .Select(g => Indexes(ctx, g.Key, g.ToImmutableArray())));

        static bool ReadsSplitField(CSharpContext ctx, ElementAccessExpressionSyntax ea)
        {
            if (ea.ArgumentList.Arguments.Count != 1) return false;
            if (!ea.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.NumericLiteralExpression)) return false;
            if (ea.Parent is AssignmentExpressionSyntax asg && asg.Left == ea) return false; // writing into an array is fine
            return IsSplit(ea.Expression)
                || (ctx.TypeOf(ea.Expression) is IArrayTypeSymbol t && t.ElementType.SpecialType == SpecialType.System_String && IsLocalFromSplit(ea));
        }

        static Finding Indexes(CSharpContext ctx, string key, ImmutableArray<ElementAccessExpressionSyntax> uses)
        {
            var indices = uses.Select(u => u.ArgumentList.Arguments[0].ToString()).Distinct().ToImmutableArray();
            var locs = uses.Select(u => ctx.Locate(u, "use")).ToImmutableArray();
            var expr = Text.Quote(uses[0].Expression.ToString());
            return new Finding
            {
                RuleId = "CNP03",
                Kind = Kind.Position,
                Title = "Fields read by index from " + expr,
                GroupKey = key,
                Locations = locs,
                Degree = indices.Length,
                FixedDistance = Distance.SameProject, // the format is defined by whoever wrote the string
                Cause = "This code reads " + Text.Count(indices.Length, "field") + " out of a split string by position (" +
                        string.Join(", ", indices.Select(i => "[" + i + "]").ToArray()) +
                        "). It depends on the exact field order chosen by whatever produced the string.",
                Suggestion = "Parse the string once, in one place next to the code that defines the format, into a small type with named properties. Or use a regex with named groups.",
                ChangeProbe = "If a field is added to or removed from the format, every index here shifts. Does the format come from somewhere that might change?",
            };
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
