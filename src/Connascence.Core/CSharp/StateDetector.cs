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
    /// CNV01: the same named constant defined separately in several types (values must stay equal).
    /// CNI01: shared mutable static state that many places read and change.
    /// </summary>
    public sealed record StateDetector : ICSharpDetector
    {
        static readonly ImmutableArray<string> MutableCollections = ImmutableArray.Create(
            "List<", "Dictionary<", "HashSet<", "Queue<", "Stack<", "SortedDictionary<", "SortedList<", "SortedSet<",
            "ConcurrentDictionary<", "ConcurrentBag<", "ConcurrentQueue<", "ConcurrentStack<", "ArrayList", "Hashtable", "IList<", "IDictionary<", "ICollection<");

        static readonly IEqualityComparer<ISymbol> SameSymbol = SymbolEqualityComparer.Default;

        public IEnumerable<Finding> Detect(CSharpContext ctx) =>
            DuplicatedConstants(ctx).Concat(SharedState(ctx));

        sealed record Constant
        {
            public required VariableDeclaratorSyntax Declarator { get; init; }
            public string? Type { get; init; }
            public required string Value { get; init; }
        }

        /// <summary>Constants with the same name (as words, ignoring "default") in different types.</summary>
        static IEnumerable<Finding> DuplicatedConstants(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                                (field.Modifiers.Any(SyntaxKind.StaticKeyword) && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)))
                .SelectMany(field => field.Declaration.Variables)
                .Select(NamedConstant)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .GroupBy(x => x.Key, x => x.Constant)
                .Select(g => DuplicatedConstant(ctx, g.Key, g.ToImmutableArray()))
                .OfType<Finding>();

        /// <summary>A constant initialised with a literal, keyed by the words of its name.</summary>
        static (string Key, Constant Constant)? NamedConstant(VariableDeclaratorSyntax v)
        {
            if (!(v.Initializer?.Value is LiteralExpressionSyntax lit)) return null;
            var words = Text.Words(v.Identifier.Text).Where(w => w != "default").OrderBy(w => w).ToImmutableArray();
            if (words.IsEmpty) return null;
            return (string.Join(" ", words), new Constant { Declarator = v, Type = CSharpContext.TypeNameOf(v), Value = lit.Token.ValueText });
        }

        static Finding? DuplicatedConstant(CSharpContext ctx, string key, ImmutableArray<Constant> all)
        {
            var consts = all.GroupBy(c => c.Type).Select(g => g.First()).ToImmutableArray();
            if (consts.Length < 2) return null;
            var locs = consts.Select(c => ctx.Locate(c.Declarator.Identifier, "definition")).ToImmutableArray();
            var name = consts[0].Declarator.Identifier.Text;
            var values = consts.Select(c => c.Value).Distinct().ToImmutableArray();
            // Different strings under the same name are usually different things (e.g. each repository's TableName).
            if (values.Length > 1 && !values.All(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))) return null;
            var where = Text.Join(consts.Select(c => c.Type + " (" + c.Value + ")").ToImmutableArray());
            return new Finding
            {
                RuleId = "CNV01",
                Kind = Kind.Value,
                Title = name + " defined " + consts.Length + " times",
                GroupKey = key,
                Locations = locs,
                Degree = consts.Length,
                Cause = name + " is defined separately in " + where + "." +
                        (values.Length > 1 ? " The values already differ, which is either a bug or two different rules sharing a name."
                                           : " They have to stay equal, but nothing links them."),
                Suggestion = "Define it once, in the type that owns the rule, and reference it from the others. If the rules really are different, give them names that say so.",
                ChangeProbe = "If this value changes, will someone know to change " + Text.All(consts.Length, "copy", "copies") + "? Is it the kind of value that gets tuned?",
            };
        }

        /// <summary>A mutable static field or property, where it is declared, and whether it is a collection (changed through calls, not assignment).</summary>
        sealed record StaticState(ISymbol Symbol, SyntaxToken Declaration, bool Collection);

        static IEnumerable<Finding> SharedState(CSharpContext ctx)
        {
            // 1. Find mutable static fields and properties.
            var declared = ctx.Roots.SelectMany(root => StaticStateIn(ctx.Model(root.SyntaxTree), root)).ToImmutableArray();
            if (declared.IsEmpty) return Enumerable.Empty<Finding>();
            var candidates = declared.GroupBy(d => d.Symbol, SameSymbol).ToImmutableDictionary(g => g.Key, g => g.Last().Declaration, SameSymbol);
            var collections = declared.Where(d => d.Collection).Select(d => d.Symbol).ToImmutableHashSet(SameSymbol);

            // 2. Find every member that touches them.
            var names = candidates.Keys.Select(k => k.Name).ToImmutableHashSet();
            return ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<IdentifierNameSyntax>())
                .Where(id => names.Contains(id.Identifier.Text))
                .Select(id => (Node: (SyntaxNode)id, Symbol: ctx.SymbolOf(id)?.OriginalDefinition))
                .Where(x => x.Symbol != null && candidates.ContainsKey(x.Symbol))
                .GroupBy(x => x.Symbol!, x => x.Node, SameSymbol)
                .Select(g => candidates.TryGetValue(g.Key, out var declaration)
                    ? SharedState(ctx, g.Key, declaration, collections.Contains(g.Key), g.ToImmutableArray())
                    : null)
                .OfType<Finding>();
        }

        static IEnumerable<StaticState> StaticStateIn(SemanticModel model, SyntaxNode root)
        {
            var fields = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
                .Where(field => !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || IsMutableCollection(field.Declaration.Type))
                .SelectMany(field => field.Declaration.Variables.Select(v => (Symbol: model.GetDeclaredSymbol(v), v.Identifier, Collection: IsMutableCollection(field.Declaration.Type))));
            var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(prop => prop.Modifiers.Any(SyntaxKind.StaticKeyword))
                .Where(prop => prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) is { } setter
                                   && !setter.Modifiers.Any(SyntaxKind.PrivateKeyword)
                               || IsMutableCollection(prop.Type))
                .Select(prop => (Symbol: (ISymbol?)model.GetDeclaredSymbol(prop), prop.Identifier, Collection: IsMutableCollection(prop.Type)));
            return fields.Concat(properties)
                .Where(x => x.Symbol != null)
                .Select(x => new StaticState(x.Symbol!, x.Identifier, x.Collection));
        }

        static Finding? SharedState(CSharpContext ctx, ISymbol symbol, SyntaxToken declaration, bool collection, ImmutableArray<SyntaxNode> uses)
        {
            // A scalar that is never assigned after its declaration is a missing readonly, not state anyone shares.
            if (!collection && !uses.Any(IsWrite)) return null;
            var useLocs = uses.Select(n => ctx.Locate(n, "use")).GroupBy(l => l.Anchor).Select(g => g.First()).ToImmutableArray();
            if (useLocs.Length < 2) return null;
            var locs = useLocs.Prepend(ctx.Locate(declaration, "declaration")).ToImmutableArray();
            var types = useLocs.Select(l => l.Type).Distinct().Count();
            var owner = symbol.ContainingType?.Name ?? "";
            return new Finding
            {
                RuleId = "CNI01",
                Kind = Kind.Identity,
                Title = "Shared mutable state " + owner + "." + symbol.Name,
                GroupKey = symbol.ToDisplayString(),
                Locations = locs,
                Degree = useLocs.Length,
                Cause = owner + "." + symbol.Name + " is static and can change. It is used in " + Text.Count(useLocs.Length, "member") +
                        (types > 1 ? " across " + types + " types" : "") +
                        ". They all depend on the same single instance and on each other's changes to it.",
                Suggestion = "Give the state one owner: make it an instance field of one class and pass that object to whoever needs it (dependency injection), or make it immutable. If it must be shared, put every read and write behind one small class.",
                ChangeProbe = "If you ever need two of these (per tenant, per request, per test) or run work in parallel, every place that touches it must change. Is that plausible?",
            };
        }

        /// <summary>x = ..., x += ..., x++, --x, ref x, out x.</summary>
        static bool IsWrite(SyntaxNode id)
        {
            var n = id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id ? ma : id;
            return n.Parent switch
            {
                AssignmentExpressionSyntax a => a.Left == n,
                PrefixUnaryExpressionSyntax pre => pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression),
                PostfixUnaryExpressionSyntax _ => true,
                ArgumentSyntax arg => arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
                _ => false,
            };
        }

        static bool IsMutableCollection(TypeSyntax? type)
        {
            if (type == null) return false;
            if (type is ArrayTypeSyntax) return true;
            var t = type.ToString();
            return MutableCollections.Any(m => t.StartsWith(m, StringComparison.Ordinal) || t.Contains("." + m));
        }
    }
}
