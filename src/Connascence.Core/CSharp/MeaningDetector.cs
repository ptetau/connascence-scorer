using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connascence.CSharp
{
    /// <summary>
    /// CNM01 / CNV02: literal values whose meaning (or limit) several places must agree on.
    /// CNM02: bare true/false arguments that the caller must decode.
    /// </summary>
    public sealed record MeaningDetector : ICSharpDetector
    {
        enum Use { Equality, Relational, Switch, Assignment, Argument, Indexer }

        sealed record Hit
        {
            public required LiteralExpressionSyntax Literal { get; init; }
            public required string Subject { get; init; }
            public Use Use { get; init; }
            public string? Owner { get; init; }   // type that declares the subject, e.g. Order for order.Status
        }

        public IEnumerable<Finding> Detect(CSharpContext ctx) =>
            MagicValues(ctx).Concat(BooleanFlags(ctx));

        // ---------------- Magic values ----------------

        /// <summary>Literals with a meaning, grouped by kind, value and subject: one finding per group.</summary>
        static IEnumerable<Finding> MagicValues(CSharpContext ctx)
        {
            var ignored = ctx.Settings.IgnoredNumbers.ToImmutableHashSet();
            var codeLike = ctx.Settings.CodeLikeNames.ToImmutableHashSet();
            return ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                .Select(lit => KeyedHit(ctx, lit, ignored, codeLike))
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .GroupBy(x => x.Key, x => x.Hit)
                .Select(g => MagicValue(ctx, g.Key, g.ToImmutableArray(), ignored, codeLike))
                .OfType<Finding>();
        }

        /// <summary>The literal's use, keyed by kind (num, str or limit), value and subject; or null if it isn't a magic value.</summary>
        static (string Key, Hit Hit)? KeyedHit(CSharpContext ctx, LiteralExpressionSyntax lit, ImmutableHashSet<string> ignored, ImmutableHashSet<string> codeLike)
        {
            bool isNumber = lit.IsKind(SyntaxKind.NumericLiteralExpression);
            bool isString = lit.IsKind(SyntaxKind.StringLiteralExpression);
            if (!isNumber && !isString) return null;

            var value = isNumber ? NumberText(lit) : lit.Token.ValueText;
            // 0, 1, 2... are usually just numbers. Compared with or assigned to a code (Status == 1) they are a meaning like any other.
            bool common = isNumber && ignored.Contains(value);
            if (isString && value.Length == 0) return null;
            if (IsNamedConstantDefinition(lit)) return null;

            var hit = Classify(ctx, lit);
            if (hit == null) return null;
            if (common && !(IsCodeLike(hit.Subject, codeLike) && (hit.Use == Use.Equality || hit.Use == Use.Switch || hit.Use == Use.Assignment))) return null;
            if (isNumber && hit.Use == Use.Indexer) return null;      // arr[3] is handled by the position detector
            if (isNumber && hit.Use == Use.Argument) return null;    // too noisy without knowing the parameter's role
            if (isString && hit.Use == Use.Argument && !IsCodeLike(hit.Subject, codeLike)) return null;

            var kindTag = isNumber && hit.Use == Use.Relational ? "limit" : (isNumber ? "num" : "str");
            var subjectKey = hit.Use == Use.Indexer ? "[]" : hit.Subject.ToLowerInvariant();
            return (kindTag + "|" + value + "|" + subjectKey, hit);
        }

        static Finding? MagicValue(CSharpContext ctx, string key, ImmutableArray<Hit> hits, ImmutableHashSet<string> ignored, ImmutableHashSet<string> codeLike)
        {
            var first = hits[0];
            bool isString = first.Literal.IsKind(SyntaxKind.StringLiteralExpression);
            bool isLimit = key.StartsWith("limit|", StringComparison.Ordinal);
            bool codeSubject = IsCodeLike(first.Subject, codeLike);
            if (hits.Length < 2 && !(codeSubject && !isLimit)) return null;
            if (hits.Length < 2 && first.Literal.IsKind(SyntaxKind.NumericLiteralExpression) && ignored.Contains(NumberText(first.Literal))) return null;

            var locs = hits.Select(h => ctx.Locate(h.Literal, "use")).ToImmutableArray();
            var value = isString ? "\"" + Text.Quote(first.Literal.Token.ValueText) + "\"" : NumberText(first.Literal);
            var subject = first.Subject;

            if (isLimit)
            {
                return new Finding
                {
                    RuleId = "CNV02",
                    Kind = Kind.Value,
                    Title = "Repeated limit " + value,
                    GroupKey = key,
                    Locations = locs,
                    Degree = locs.Length,
                    Cause = "The limit " + value + " is checked against " + subject + " " + Text.Spread(locs) +
                            ". These checks must stay equal, but nothing ties them together.",
                    Suggestion = "Name the limit once, in the type that owns the rule (for example `const int Max" + Text.PascalCase(subject) +
                                 " = " + value + ";`), and reference that constant from every check. Better still, move the check into one method or a value type that validates itself.",
                    ChangeProbe = "If the limit changes (a business rule, a column size, a vendor limit), will someone find " +
                                  Text.All(locs.Length, "check") + "? Is a change like that plausible?",
                };
            }

            var suggestion =
                first.Use == Use.Indexer
                    ? "Keep the key " + value + " in one place: bind it once to a typed options class, or make it a constant next to the code that owns it."
                : !isString && codeSubject
                    ? "Replace " + value + " with a named enum member (for example `" + EnumName(first, hits) +
                      ".Shipped`, using the real name) and make " + subject + " that enum type, so the meaning lives in one place and the compiler checks it."
                : isString && codeSubject
                    ? "Replace " + value + " with an enum, or a constant in one shared place (for example `" + EnumName(first, hits) + "s." +
                      Text.PascalCase(first.Literal.Token.ValueText) + "`), and use it everywhere."
                    : "Give " + value + " a name once (a const or static readonly) next to the code that owns its meaning, and use that name in every place.";

            var verb = first.Use == Use.Indexer ? "used as a lookup key" :
                       first.Use == Use.Switch ? "used as a case for " + subject :
                       first.Use == Use.Assignment ? "assigned to or compared with " + subject :
                       "compared with " + subject;

            return new Finding
            {
                RuleId = "CNM01",
                Kind = Kind.Meaning,
                Title = "Magic value " + value,
                GroupKey = key,
                Locations = locs,
                Degree = locs.Length,
                Cause = "The value " + value + " is " + verb + " " + Text.Spread(locs) +
                        (locs.Length == 1 ? ". Whoever sets " + subject + " elsewhere must know what " + value + " means." :
                        ". Every one of these places has to know what " + value + " means."),
                Suggestion = suggestion,
                ChangeProbe = "If the meaning of " + value + " changes, or a new value is added next to it, " + Text.All(locs.Length, "place") +
                              " (and whoever produces the value) must change together. Is a change like that plausible?",
            };
        }

        static bool IsCodeLike(string subject, ImmutableHashSet<string> codeLike)
        {
            if (string.IsNullOrEmpty(subject)) return false;
            var words = Text.Words(subject);
            return words.Count > 0 && codeLike.Contains(words[words.Count - 1]);
        }

        static string NumberText(LiteralExpressionSyntax lit)
        {
            var v = lit.Token.Value;
            string text = v is double d ? d.ToString(CultureInfo.InvariantCulture)
                        : v is float f ? f.ToString(CultureInfo.InvariantCulture)
                        : v is decimal m ? m.ToString(CultureInfo.InvariantCulture)
                        : System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            return lit.Parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.UnaryMinusExpression) ? "-" + text : text;
        }

        /// <summary>Literals that define a named constant are the fix, not the problem.</summary>
        static bool IsNamedConstantDefinition(LiteralExpressionSyntax lit) =>
            lit.Ancestors().Select(DefinesConstant).FirstOrDefault(answer => answer.HasValue) ?? false;

        /// <summary>Whether this ancestor makes the literal a named constant's definition, or null to keep looking further out.</summary>
        static bool? DefinesConstant(SyntaxNode a) => a switch
        {
            AttributeSyntax _ => true,
            EnumMemberDeclarationSyntax _ => true,
            FieldDeclarationSyntax fd => fd.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                                         (fd.Modifiers.Any(SyntaxKind.StaticKeyword) && fd.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)),
            LocalDeclarationStatementSyntax ld => ld.IsConst,
            ParameterSyntax _ => true, // default parameter values
            StatementSyntax _ => false,
            MemberDeclarationSyntax _ => false,
            _ => null,
        };

        static Hit? Classify(CSharpContext ctx, LiteralExpressionSyntax lit)
        {
            var node = Unwrapped(lit.Parent is PrefixUnaryExpressionSyntax sign ? sign : lit);
            var parent = node.Parent;

            switch (parent)
            {
                case BinaryExpressionSyntax b when IsComparison(b.Kind()):
                {
                    var other = b.Left == node ? b.Right : b.Left;
                    var name = NameOf(other);
                    if (name == null) return null;
                    var rel = !(b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression));
                    return new Hit { Literal = lit, Subject = name, Use = rel ? Use.Relational : Use.Equality, Owner = OwnerOf(ctx, other) };
                }
                case ConstantPatternSyntax cp:
                {
                    var subject = SwitchSubject(cp);
                    return subject == null ? null : new Hit { Literal = lit, Subject = subject, Use = Use.Switch };
                }
                case RelationalPatternSyntax rp:
                {
                    var subject = SwitchSubject(rp);
                    return subject == null ? null : new Hit { Literal = lit, Subject = subject, Use = Use.Relational };
                }
                case CaseSwitchLabelSyntax cl:
                {
                    var sw = cl.Ancestors().OfType<SwitchStatementSyntax>().FirstOrDefault();
                    var subject = sw == null ? null : NameOf(sw.Expression);
                    return subject == null ? null : new Hit { Literal = lit, Subject = subject, Use = Use.Switch };
                }
                case AssignmentExpressionSyntax asg when asg.Right == node && asg.IsKind(SyntaxKind.SimpleAssignmentExpression):
                {
                    var name = NameOf(asg.Left);
                    return name == null ? null : new Hit { Literal = lit, Subject = name, Use = Use.Assignment, Owner = OwnerOf(ctx, asg.Left) };
                }
                case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax vd:
                    return new Hit { Literal = lit, Subject = vd.Identifier.Text, Use = Use.Assignment };
                case ArgumentSyntax arg:
                {
                    if (arg.Parent is BracketedArgumentListSyntax)
                        return new Hit { Literal = lit, Subject = "[]", Use = Use.Indexer };
                    var subject = ParameterName(ctx, arg);
                    return subject == null ? null : new Hit { Literal = lit, Subject = subject, Use = Use.Argument };
                }
            }
            return null;
        }

        /// <summary>The outermost of the parentheses and casts around a node: (int)(x) is used where the outer cast is.</summary>
        static SyntaxNode Unwrapped(SyntaxNode node) =>
            node.Parent is ParenthesizedExpressionSyntax || node.Parent is CastExpressionSyntax ? Unwrapped(node.Parent) : node;

        static string? OwnerOf(CSharpContext ctx, ExpressionSyntax e)
        {
            var s = ctx.SymbolOf(e);
            return (s is IPropertySymbol || s is IFieldSymbol) ? s.ContainingType?.Name : null;
        }

        static string EnumName(Hit h, ImmutableArray<Hit> group)
        {
            var subject = Text.PascalCase(h.Subject);
            var owner = group.Select(x => x.Owner).FirstOrDefault(o => o != null);
            return owner != null && !subject.StartsWith(owner, StringComparison.Ordinal) ? owner + subject : subject;
        }

        static bool IsComparison(SyntaxKind k) =>
            k == SyntaxKind.EqualsExpression || k == SyntaxKind.NotEqualsExpression ||
            k == SyntaxKind.LessThanExpression || k == SyntaxKind.LessThanOrEqualExpression ||
            k == SyntaxKind.GreaterThanExpression || k == SyntaxKind.GreaterThanOrEqualExpression;

        static string? SwitchSubject(SyntaxNode pattern) =>
            pattern.Ancestors().Select(a => a switch
                {
                    SwitchExpressionSyntax se => (Found: true, Name: NameOf(se.GoverningExpression)),
                    SwitchStatementSyntax ss => (Found: true, Name: NameOf(ss.Expression)),
                    IsPatternExpressionSyntax ip => (Found: true, Name: NameOf(ip.Expression)),
                    StatementSyntax _ => (Found: true, Name: (string?)null),
                    _ => (Found: false, Name: (string?)null),
                })
                .FirstOrDefault(x => x.Found).Name;

        /// <summary>The name a value is compared with: x, obj.Status, GetStatus() all give the last identifier.</summary>
        public static string? NameOf(ExpressionSyntax? e)
        {
            switch (e)
            {
                case IdentifierNameSyntax id: return id.Identifier.Text;
                case MemberAccessExpressionSyntax ma: return ma.Name.Identifier.Text;
                case InvocationExpressionSyntax inv: return NameOf(inv.Expression);
                case ConditionalAccessExpressionSyntax ca: return NameOf(ca.WhenNotNull);
                case MemberBindingExpressionSyntax mb: return mb.Name.Identifier.Text;
                case ParenthesizedExpressionSyntax p: return NameOf(p.Expression);
                case CastExpressionSyntax c: return NameOf(c.Expression);
                case ElementAccessExpressionSyntax ea: return NameOf(ea.Expression);
                case ThisExpressionSyntax _: return "this";
            }
            return null;
        }

        static string? ParameterName(CSharpContext ctx, ArgumentSyntax arg)
        {
            if (arg.NameColon != null) return arg.NameColon.Name.Identifier.Text;
            var list = arg.Parent as ArgumentListSyntax;
            if (list == null) return null;
            var method = ctx.SymbolOf(list.Parent) as IMethodSymbol;
            if (method == null) return null;
            var index = list.Arguments.IndexOf(arg);
            if (index < 0 || method.Parameters.Length == 0) return null;
            return method.Parameters[Math.Min(index, method.Parameters.Length - 1)].Name;   // a params argument maps to the last one
        }

        // ---------------- Boolean flags ----------------

        static IEnumerable<Finding> BooleanFlags(CSharpContext ctx) =>
            ctx.Calls.Select(c => BooleanFlags(ctx, c.Method, c.Sites)).OfType<Finding>();

        /// <summary>The position of the first bare true/false argument in the call, or -1.</summary>
        static int FlagIndex(CallSite call) =>
            call.Args.Arguments.Select((a, i) => (Arg: a, Index: i))
                .Where(x => x.Arg.NameColon == null &&
                            (x.Arg.Expression.IsKind(SyntaxKind.TrueLiteralExpression) || x.Arg.Expression.IsKind(SyntaxKind.FalseLiteralExpression)))
                .Select(x => x.Index).DefaultIfEmpty(-1).First();

        static Finding? BooleanFlags(CSharpContext ctx, IMethodSymbol method, ImmutableArray<CallSite> sites)
        {
            var flaggedAt = sites.Select(call => (Call: call, Index: FlagIndex(call))).Where(x => x.Index >= 0).ToImmutableArray();
            if (flaggedAt.IsEmpty) return null;
            var flagged = flaggedAt.Select(x => x.Call).ToImmutableArray();
            var flagParams = flaggedAt.Where(x => x.Index < method.Parameters.Length).Select(x => method.Parameters[x.Index].Name).Distinct().ToImmutableArray();

            var decl = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var declaration = decl != null && ctx.FileOf(decl.SyntaxTree) != null
                ? ImmutableArray.Create(ctx.Locate(decl is MethodDeclarationSyntax md ? md.Identifier
                                                   : decl is ConstructorDeclarationSyntax cd ? cd.Identifier : decl.GetFirstToken(), "declaration"))
                : ImmutableArray<CodeLocation>.Empty;
            var locs = declaration.Concat(flagged.Select(c => ctx.Locate(c.Node, "call site"))).ToImmutableArray();

            var name = method.MethodKind == MethodKind.Constructor ? "new " + method.ContainingType.Name : method.Name;
            var param = flagParams.Length > 0 ? string.Join(", ", flagParams) : "a bool parameter";
            var callers = flagged.Length;
            return new Finding
            {
                RuleId = "CNM02",
                Kind = Kind.Meaning,
                Title = "Boolean flag argument to " + name,
                GroupKey = method.ToDisplayString(),
                Locations = locs,
                Degree = callers,
                Cause = Text.CountVerb(callers, "caller", "passes", "pass") + " a bare true/false for " + param + " to " + name +
                        "(). Each caller has to know what true means, and the code doesn't say.",
                Suggestion = "Split " + name + " into two well-named methods, or replace the bool with an enum (for example `" + name +
                             "(" + Text.PascalCase(param.Split(',')[0]) + "Mode.X)`). Quick step: have callers use a named argument (`" +
                             (flagParams.FirstOrDefault() ?? "flag") + ": true`).",
                ChangeProbe = "If a third option is needed, or what the flag means changes, every caller passing a bare true/false must be revisited. Is that plausible?",
            };
        }
    }
}
