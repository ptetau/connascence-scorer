using System.Collections.Generic;
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
    public sealed class StateDetector : ICSharpDetector
    {
        static readonly string[] MutableCollections =
        {
            "List<", "Dictionary<", "HashSet<", "Queue<", "Stack<", "SortedDictionary<", "SortedList<", "SortedSet<",
            "ConcurrentDictionary<", "ConcurrentBag<", "ConcurrentQueue<", "ConcurrentStack<", "ArrayList", "Hashtable", "IList<", "IDictionary<", "ICollection<",
        };

        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return DuplicatedConstants(ctx).Concat(SharedState(ctx));
        }

        sealed class Constant
        {
            public VariableDeclaratorSyntax Declarator;
            public string Type;
            public string Value;
        }

        IEnumerable<Finding> DuplicatedConstants(CSharpContext ctx)
        {
            var groups = new Dictionary<string, List<Constant>>();
            foreach (var root in ctx.Roots)
            {
                foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                {
                    bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                                   (field.Modifiers.Any(SyntaxKind.StaticKeyword) && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword));
                    if (!isConst) continue;
                    foreach (var v in field.Declaration.Variables)
                    {
                        if (!(v.Initializer?.Value is LiteralExpressionSyntax lit)) continue;
                        var words = Text.Words(v.Identifier.Text).Where(w => w != "default").OrderBy(w => w).ToList();
                        if (words.Count == 0) continue;
                        var key = string.Join(" ", words.ToArray());
                        List<Constant> list;
                        if (!groups.TryGetValue(key, out list)) { list = new List<Constant>(); groups[key] = list; }
                        list.Add(new Constant { Declarator = v, Type = CSharpContext.TypeNameOf(v), Value = lit.Token.ValueText });
                    }
                }
            }

            foreach (var kv in groups)
            {
                var consts = kv.Value.GroupBy(c => c.Type).Select(g => g.First()).ToList();
                if (consts.Count < 2) continue;
                var locs = consts.Select(c => ctx.Locate(c.Declarator.Identifier, Roles.Definition)).ToList();
                var names = consts.Select(c => c.Declarator.Identifier.Text).Distinct().ToList();
                var values = consts.Select(c => c.Value).Distinct().ToList();
                // Different strings under the same name are usually different things (e.g. each repository's TableName).
                if (values.Count > 1 && !values.All(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))) continue;
                var where = Text.Join(consts.Select(c => c.Type + " (" + c.Value + ")").ToList());
                yield return new Finding
                {
                    RuleId = "CNV01",
                    Kind = Kind.Value,
                    Title = names[0] + " defined " + consts.Count + " times",
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = consts.Count,
                    Cause = names[0] + " is defined separately in " + where + "." +
                            (values.Count > 1 ? " The values already differ, which is either a bug or two different rules sharing a name."
                                              : " They have to stay equal, but nothing links them."),
                    Suggestion = "Define it once, in the type that owns the rule, and reference it from the others. If the rules really are different, give them names that say so.",
                    ChangeProbe = "If this value changes, will someone know to change " + Text.All(consts.Count, "copy", "copies") + "? Is it the kind of value that gets tuned?",
                };
            }
        }

        IEnumerable<Finding> SharedState(CSharpContext ctx)
        {
            // 1. Find mutable static fields and properties.
            var candidates = new Dictionary<ISymbol, SyntaxToken>(SymbolEqualityComparer.Default);
            var collections = new HashSet<ISymbol>(SymbolEqualityComparer.Default);   // changed through calls (Add, indexers), not assignment
            foreach (var root in ctx.Roots)
            {
                var model = ctx.Model(root.SyntaxTree);
                foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                {
                    if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
                    bool readOnly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
                    if (readOnly && !IsMutableCollection(field.Declaration.Type)) continue;
                    foreach (var v in field.Declaration.Variables)
                    {
                        var s = model.GetDeclaredSymbol(v);
                        if (s == null) continue;
                        candidates[s] = v.Identifier;
                        if (IsMutableCollection(field.Declaration.Type)) collections.Add(s);
                    }
                }
                foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                {
                    if (!prop.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
                    var setter = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                    bool mutable = setter != null && !setter.Modifiers.Any(SyntaxKind.PrivateKeyword) || IsMutableCollection(prop.Type);
                    if (!mutable) continue;
                    var s = model.GetDeclaredSymbol(prop);
                    if (s == null) continue;
                    candidates[s] = prop.Identifier;
                    if (IsMutableCollection(prop.Type)) collections.Add(s);
                }
            }
            if (candidates.Count == 0) yield break;

            // 2. Find every member that touches them.
            var names = new HashSet<string>(candidates.Keys.Select(k => k.Name));
            var uses = new Dictionary<ISymbol, List<SyntaxNode>>(SymbolEqualityComparer.Default);
            foreach (var root in ctx.Roots)
            {
                foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!names.Contains(id.Identifier.Text)) continue;
                    var s = ctx.SymbolOf(id);
                    if (s == null || !candidates.ContainsKey(s.OriginalDefinition)) continue;
                    List<SyntaxNode> list;
                    if (!uses.TryGetValue(s.OriginalDefinition, out list)) { list = new List<SyntaxNode>(); uses[s.OriginalDefinition] = list; }
                    list.Add(id);
                }
            }

            foreach (var kv in uses)
            {
                var symbol = kv.Key;
                // A scalar that is never assigned after its declaration is a missing readonly, not state anyone shares.
                if (!collections.Contains(symbol) && !kv.Value.Any(IsWrite)) continue;
                var useLocs = kv.Value.Select(n => ctx.Locate(n, Roles.Use)).GroupBy(l => l.Anchor).Select(g => g.First()).ToList();
                if (useLocs.Count < 2) continue;
                var locs = new List<CodeLocation> { ctx.Locate(candidates[symbol], Roles.Declaration) };
                locs.AddRange(useLocs);
                var types = useLocs.Select(l => l.Type).Distinct().Count();
                var owner = symbol.ContainingType?.Name ?? "";
                yield return new Finding
                {
                    RuleId = "CNI01",
                    Kind = Kind.Identity,
                    Title = "Shared mutable state " + owner + "." + symbol.Name,
                    GroupKey = symbol.ToDisplayString(),
                    Locations = locs,
                    Degree = useLocs.Count,
                    Cause = owner + "." + symbol.Name + " is static and can change. It is used in " + Text.Count(useLocs.Count, "member") +
                            (types > 1 ? " across " + types + " types" : "") +
                            ". They all depend on the same single instance and on each other's changes to it.",
                    Suggestion = "Give the state one owner: make it an instance field of one class and pass that object to whoever needs it (dependency injection), or make it immutable. If it must be shared, put every read and write behind one small class.",
                    ChangeProbe = "If you ever need two of these (per tenant, per request, per test) or run work in parallel, every place that touches it must change. Is that plausible?",
                };
            }
        }

        /// <summary>x = ..., x += ..., x++, --x, ref x, out x.</summary>
        static bool IsWrite(SyntaxNode id)
        {
            SyntaxNode n = id;
            if (n.Parent is MemberAccessExpressionSyntax ma && ma.Name == n) n = ma;
            switch (n.Parent)
            {
                case AssignmentExpressionSyntax a: return a.Left == n;
                case PrefixUnaryExpressionSyntax pre: return pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression);
                case PostfixUnaryExpressionSyntax _: return true;
                case ArgumentSyntax arg: return arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
            }
            return false;
        }

        static bool IsMutableCollection(TypeSyntax type)
        {
            if (type == null) return false;
            if (type is ArrayTypeSyntax) return true;
            var t = type.ToString();
            return MutableCollections.Any(m => t.StartsWith(m) || t.Contains("." + m));
        }
    }
}
