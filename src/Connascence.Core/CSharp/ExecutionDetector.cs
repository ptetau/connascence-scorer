using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connascence.CSharp
{
    /// <summary>
    /// CNE01: a class that must be set up by calling a method before it can be used.
    /// CNT01: code that waits a fixed time and hopes something else has finished.
    /// </summary>
    public sealed class ExecutionDetector : ICSharpDetector
    {
        static readonly string[] StateWords = { "initiali", "open", "started", "connected", "loaded", "configured", "ready", "prepared", "begun" };

        public IEnumerable<Finding> Detect(CSharpContext ctx)
        {
            return InitOrder(ctx).Concat(FixedWaits(ctx));
        }

        IEnumerable<Finding> InitOrder(CSharpContext ctx)
        {
            var prefixes = ctx.Settings.InitMethodPrefixes;
            foreach (var root in ctx.Roots)
            {
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (cls.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
                    var publicMethods = cls.Members.OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword) && !m.Modifiers.Any(SyntaxKind.OverrideKeyword))
                        .ToList();
                    var inits = publicMethods.Where(m => IsInitName(m.Identifier.Text, prefixes)).ToList();
                    if (inits.Count == 0) continue;
                    var others = publicMethods.Except(inits).ToList();
                    if (others.Count == 0) continue;

                    // Evidence that order matters: a state flag, or a guard that throws InvalidOperationException.
                    var guards = cls.DescendantNodes().OfType<ThrowStatementSyntax>().Cast<SyntaxNode>()
                        .Concat(cls.DescendantNodes().OfType<ThrowExpressionSyntax>())
                        .Where(t => t.ToString().Contains("InvalidOperationException"))
                        .Where(t => !inits.Any(i => i.Span.Contains(t.Span)))
                        .ToList();
                    var flags = cls.Members.OfType<FieldDeclarationSyntax>()
                        .SelectMany(f => f.Declaration.Variables)
                        .Where(v => StateWords.Any(w => v.Identifier.Text.ToLowerInvariant().Contains(w)))
                        .ToList();

                    var model = ctx.Model(cls.SyntaxTree);
                    var className = cls.Identifier.Text;
                    foreach (var init in inits)
                    {
                        var symbol = model.GetDeclaredSymbol(init);
                        var external = ctx.CallsTo(symbol)
                            .Where(c => CSharpContext.TypeNameOf(c.Node) != CSharpContext.TypeNameOf(init))
                            .ToList();
                        bool evidence = guards.Count > 0 || flags.Count > 0;
                        if (!evidence && external.Count < 2) continue;

                        var locs = new List<CodeLocation> { ctx.Locate(init.Identifier, Roles.SetupMethod) };
                        locs.AddRange(guards.Take(5).Select(g => ctx.Locate(g, Roles.RuntimeGuard)));
                        locs.AddRange(external.Select(c => ctx.Locate(c.Node, Roles.Caller)));
                        var otherNames = Text.Join(others.Select(o => o.Identifier.Text + "()").Distinct().Take(3).ToList());
                        var degree = System.Math.Max(1, external.Count);
                        yield return new Finding
                        {
                            RuleId = "CNE01",
                            Kind = Kind.Execution,
                            Title = className + "." + init.Identifier.Text + "() must be called first",
                            GroupKey = className + "." + init.Identifier.Text,
                            Locations = locs,
                            Degree = degree,
                            Cause = "Callers must call " + init.Identifier.Text + "() before " + otherNames + ". " +
                                    (external.Count == 1 ? "1 caller in another class has to remember this. " : external.Count > 1 ? external.Count + " callers in other classes each have to remember this. " : "") +
                                    (evidence ? "The class only finds out at runtime (" + (flags.Count > 0 ? "it tracks a " + flags[0].Identifier.Text + " flag" : "it throws InvalidOperationException") + ")."
                                              : "Nothing in the types stops a caller getting the order wrong."),
                            Suggestion = "Make the wrong order impossible: do the setup in the constructor or a static factory (for example `" + className + ".Create" +
                                         (init.Identifier.Text.StartsWith("Open") || init.Identifier.Text.StartsWith("Connect") ? "Connected" : "") +
                                         "Async()`), or have " + init.Identifier.Text + "() return a different type that is the only one with " + otherNames + ".",
                            ChangeProbe = "If setup gains another step, or more callers are added, each one must get the order right by hand. Is this class likely to be used in new places?",
                        };
                    }
                }
            }
        }

        static bool IsInitName(string name, IList<string> prefixes)
        {
            foreach (var p in prefixes)
            {
                if (!name.StartsWith(p, System.StringComparison.Ordinal)) continue;
                if (name.Length == p.Length) return true;
                var next = name[p.Length];
                if (char.IsUpper(next) && !name.Substring(p.Length).StartsWith("Async") || name.Substring(p.Length) == "Async") return true;
            }
            return false;
        }

        IEnumerable<Finding> FixedWaits(CSharpContext ctx)
        {
            foreach (var root in ctx.Roots)
            {
                foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var ma = inv.Expression as MemberAccessExpressionSyntax;
                    if (ma == null) continue;
                    var target = ma.Expression.ToString();
                    var name = ma.Name.Identifier.Text;
                    bool sleep = target.EndsWith("Thread") && name == "Sleep";
                    bool delay = target.EndsWith("Task") && name == "Delay";
                    if (!sleep && !delay) continue;
                    var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "";
                    if (arg == "0" || arg.Contains("Infinite")) continue;

                    var loc = ctx.Locate(inv, Roles.Wait);
                    var inLoop = inv.Ancestors().Any(a => a is WhileStatementSyntax || a is ForStatementSyntax || a is DoStatementSyntax || a is ForEachStatementSyntax);
                    yield return new Finding
                    {
                        RuleId = "CNT01",
                        Kind = Kind.Timing,
                        Title = (sleep ? "Thread.Sleep" : "Task.Delay") + "(" + Text.Quote(arg) + ")",
                        GroupKey = loc.Anchor + "|" + inv.ToString(),
                        Locations = new List<CodeLocation> { loc },
                        Degree = 1,
                        FixedDistance = Distance.SameProject, // the other party is whatever is being waited for
                        Cause = "This waits a fixed " + arg + (System.Text.RegularExpressions.Regex.IsMatch(arg, "^[0-9_]+$") ? " ms" : "") + (inLoop ? " inside a loop" : "") +
                                ", assuming the other work will be done by then. That timing is an unwritten agreement with code running elsewhere.",
                        Suggestion = inLoop
                            ? "If this is polling or retry back-off, put the timings in one retry policy (for example Polly) so they're set in one place. Better still, wait on the real signal."
                            : "Wait for the actual event instead: await the task, use a TaskCompletionSource, SemaphoreSlim or channel, or poll a condition with a timeout.",
                        ChangeProbe = "If the other side gets slower (more data, slower network, a busy CI box), does this break? Will load or deployment changes make that likely?",
                    };
                }
            }
        }
    }
}
