using System.Collections.Generic;
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
    public sealed class MeaningDetector : ICSharpDetector
    {
        enum Use { Equality, Relational, Switch, Assignment, Argument, Indexer }

        sealed class Hit
        {
            public LiteralExpressionSyntax Literal;
            public string Subject;
            public Use Use;
            public string Owner;   // type that declares the subject, e.g. Order for order.Status
            public List<Hit> Hits; // the whole group, set on the first hit
        }

        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return MagicValues(ctx).Concat(BooleanFlags(ctx));
        }

        // ---------------- Magic values ----------------

        IEnumerable<Finding> MagicValues(CSharpContext ctx)
        {
            var s = ctx.Settings;
            var ignored = new HashSet<string>(s.IgnoredNumbers);
            var codeLike = new HashSet<string>(s.CodeLikeNames);
            var groups = new Dictionary<string, List<Hit>>();

            foreach (var root in ctx.Roots)
            {
                foreach (var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
                {
                    bool isNumber = lit.IsKind(SyntaxKind.NumericLiteralExpression);
                    bool isString = lit.IsKind(SyntaxKind.StringLiteralExpression);
                    if (!isNumber && !isString) continue;

                    var value = isNumber ? NumberText(lit) : lit.Token.ValueText;
                    if (isNumber && ignored.Contains(value)) continue;
                    if (isString && value.Length == 0) continue;
                    if (IsNamedConstantDefinition(lit)) continue;

                    var hit = Classify(ctx, lit);
                    if (hit == null) continue;
                    if (isNumber && hit.Use == Use.Indexer) continue;      // arr[3] is handled by the position detector
                    if (isNumber && hit.Use == Use.Argument) continue;    // too noisy without knowing the parameter's role
                    if (isString && hit.Use == Use.Argument && !IsCodeLike(hit.Subject, codeLike)) continue;

                    var kindTag = isNumber && hit.Use == Use.Relational ? "limit" : (isNumber ? "num" : "str");
                    var subjectKey = hit.Use == Use.Indexer ? "[]" : hit.Subject.ToLowerInvariant();
                    var key = kindTag + "|" + value + "|" + subjectKey;
                    List<Hit> list;
                    if (!groups.TryGetValue(key, out list)) { list = new List<Hit>(); groups[key] = list; }
                    list.Add(hit);
                }
            }

            foreach (var kv in groups)
            {
                var hits = kv.Value;
                var first = hits[0];
                first.Hits = hits;
                bool isString = first.Literal.IsKind(SyntaxKind.StringLiteralExpression);
                bool isLimit = kv.Key.StartsWith("limit|");
                bool codeSubject = IsCodeLike(first.Subject, codeLike);
                if (hits.Count < 2 && !(codeSubject && !isLimit)) continue;

                var locs = hits.Select(h => ctx.Locate(h.Literal, "use")).ToList();
                var value = isString ? "\"" + Text.Quote(first.Literal.Token.ValueText) + "\"" : NumberText(first.Literal);
                var subject = first.Subject;

                if (isLimit)
                {
                    yield return new Finding
                    {
                        RuleId = "CNV02",
                        Kind = Kind.Value,
                        Title = "Repeated limit " + value,
                        GroupKey = kv.Key,
                        Locations = locs,
                        Degree = locs.Count,
                        Cause = "The limit " + value + " is checked against " + subject + " " + Text.Spread(locs) +
                                ". These checks must stay equal, but nothing ties them together.",
                        Suggestion = "Name the limit once, in the type that owns the rule (for example `const int Max" + Text.PascalCase(subject) +
                                     " = " + value + ";`), and reference that constant from every check. Better still, move the check into one method or a value type that validates itself.",
                        ChangeProbe = "If the limit changes (a business rule, a column size, a vendor limit), will someone find " +
                                      Text.All(locs.Count, "check") + "? Is a change like that plausible?",
                    };
                    continue;
                }

                string suggestion;
                if (first.Use == Use.Indexer)
                    suggestion = "Keep the key " + value + " in one place: bind it once to a typed options class, or make it a constant next to the code that owns it.";
                else if (!isString && codeSubject)
                    suggestion = "Replace " + value + " with a named enum member (for example `" + EnumName(first) +
                                 ".Shipped`, using the real name) and make " + subject + " that enum type, so the meaning lives in one place and the compiler checks it.";
                else if (isString && codeSubject)
                    suggestion = "Replace " + value + " with an enum, or a constant in one shared place (for example `" + EnumName(first) + "s." +
                                 Text.PascalCase(first.Literal.Token.ValueText) + "`), and use it everywhere.";
                else
                    suggestion = "Give " + value + " a name once (a const or static readonly) next to the code that owns its meaning, and use that name in every place.";

                var verb = first.Use == Use.Indexer ? "used as a lookup key" :
                           first.Use == Use.Switch ? "used as a case for " + subject :
                           first.Use == Use.Assignment ? "assigned to or compared with " + subject :
                           "compared with " + subject;

                yield return new Finding
                {
                    RuleId = "CNM01",
                    Kind = Kind.Meaning,
                    Title = "Magic value " + value,
                    GroupKey = kv.Key,
                    Locations = locs,
                    Degree = locs.Count,
                    Cause = "The value " + value + " is " + verb + " " + Text.Spread(locs) +
                            (locs.Count == 1 ? ". Whoever sets " + subject + " elsewhere must know what " + value + " means." :
                            ". Every one of these places has to know what " + value + " means."),
                    Suggestion = suggestion,
                    ChangeProbe = "If the meaning of " + value + " changes, or a new value is added next to it, " + Text.All(locs.Count, "place") +
                                  " (and whoever produces the value) must change together. Is a change like that plausible?",
                };
            }
        }

        static bool IsCodeLike(string subject, HashSet<string> codeLike)
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
                        : System.Convert.ToString(v, CultureInfo.InvariantCulture);
            if (lit.Parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.UnaryMinusExpression)) text = "-" + text;
            return text;
        }

        /// <summary>Literals that define a named constant are the fix, not the problem.</summary>
        static bool IsNamedConstantDefinition(LiteralExpressionSyntax lit)
        {
            foreach (var a in lit.Ancestors())
            {
                switch (a)
                {
                    case AttributeSyntax _: return true;
                    case EnumMemberDeclarationSyntax _: return true;
                    case FieldDeclarationSyntax fd:
                        return fd.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                               (fd.Modifiers.Any(SyntaxKind.StaticKeyword) && fd.Modifiers.Any(SyntaxKind.ReadOnlyKeyword));
                    case LocalDeclarationStatementSyntax ld: return ld.IsConst;
                    case ParameterSyntax _: return true; // default parameter values
                    case StatementSyntax _: return false;
                    case MemberDeclarationSyntax _: return false;
                }
            }
            return false;
        }

        static Hit Classify(CSharpContext ctx, LiteralExpressionSyntax lit)
        {
            SyntaxNode node = lit;
            if (node.Parent is PrefixUnaryExpressionSyntax) node = node.Parent;
            while (node.Parent is ParenthesizedExpressionSyntax || node.Parent is CastExpressionSyntax) node = node.Parent;
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

        static string OwnerOf(CSharpContext ctx, ExpressionSyntax e)
        {
            var s = ctx.SymbolOf(e);
            return (s is IPropertySymbol || s is IFieldSymbol) ? s.ContainingType?.Name : null;
        }

        static string EnumName(Hit h)
        {
            var subject = Text.PascalCase(h.Subject);
            var owner = h.Hits?.Select(x => x.Owner).FirstOrDefault(o => o != null);
            return owner != null && !subject.StartsWith(owner) ? owner + subject : subject;
        }

        static bool IsComparison(SyntaxKind k) =>
            k == SyntaxKind.EqualsExpression || k == SyntaxKind.NotEqualsExpression ||
            k == SyntaxKind.LessThanExpression || k == SyntaxKind.LessThanOrEqualExpression ||
            k == SyntaxKind.GreaterThanExpression || k == SyntaxKind.GreaterThanOrEqualExpression;

        static string SwitchSubject(SyntaxNode pattern)
        {
            foreach (var a in pattern.Ancestors())
            {
                if (a is SwitchExpressionSyntax se) return NameOf(se.GoverningExpression);
                if (a is SwitchStatementSyntax ss) return NameOf(ss.Expression);
                if (a is IsPatternExpressionSyntax ip) return NameOf(ip.Expression);
                if (a is StatementSyntax) return null;
            }
            return null;
        }

        /// <summary>The name a value is compared with: x, obj.Status, GetStatus() all give the last identifier.</summary>
        public static string NameOf(ExpressionSyntax e)
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

        static string ParameterName(CSharpContext ctx, ArgumentSyntax arg)
        {
            if (arg.NameColon != null) return arg.NameColon.Name.Identifier.Text;
            var list = arg.Parent as ArgumentListSyntax;
            if (list == null) return null;
            var method = ctx.SymbolOf(list.Parent) as IMethodSymbol;
            if (method == null) return null;
            var index = list.Arguments.IndexOf(arg);
            if (index < 0 || method.Parameters.Length == 0) return null;
            if (index >= method.Parameters.Length) index = method.Parameters.Length - 1;
            return method.Parameters[index].Name;
        }

        // ---------------- Boolean flags ----------------

        IEnumerable<Finding> BooleanFlags(CSharpContext ctx)
        {
            foreach (var kv in ctx.Calls)
            {
                var method = kv.Key;
                var flagged = new List<CallSite>();
                var flagParams = new HashSet<string>();
                foreach (var call in kv.Value)
                {
                    for (int i = 0; i < call.Args.Arguments.Count; i++)
                    {
                        var a = call.Args.Arguments[i];
                        if (a.NameColon != null) continue;
                        if (!(a.Expression.IsKind(SyntaxKind.TrueLiteralExpression) || a.Expression.IsKind(SyntaxKind.FalseLiteralExpression))) continue;
                        flagged.Add(call);
                        if (i < method.Parameters.Length) flagParams.Add(method.Parameters[i].Name);
                        break;
                    }
                }
                if (flagged.Count == 0) continue;

                var decl = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                var locs = new List<CodeLocation>();
                if (decl != null && ctx.FileOf(decl.SyntaxTree) != null)
                {
                    var tok = decl is MethodDeclarationSyntax md ? md.Identifier
                            : decl is ConstructorDeclarationSyntax cd ? cd.Identifier : decl.GetFirstToken();
                    locs.Add(ctx.Locate(tok, "declaration"));
                }
                locs.AddRange(flagged.Select(c => ctx.Locate(c.Node, "call site")));

                var name = method.MethodKind == MethodKind.Constructor ? "new " + method.ContainingType.Name : method.Name;
                var param = flagParams.Count > 0 ? string.Join(", ", flagParams.ToArray()) : "a bool parameter";
                var callers = flagged.Count;
                yield return new Finding
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
}
