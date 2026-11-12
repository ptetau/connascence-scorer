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
    /// CNE01: a class that must be set up by calling a method before it can be used.
    /// CNT01: code that waits a fixed time and hopes something else has finished.
    /// </summary>
    public sealed record ExecutionDetector : ICSharpDetector
    {
        static readonly ImmutableArray<string> StateWords =
            ImmutableArray.Create("initiali", "open", "started", "connected", "loaded", "configured", "ready", "prepared", "begun");

        public IEnumerable<Finding> Detect(CSharpContext ctx) =>
            InitOrder(ctx).Concat(FixedWaits(ctx));

        static IEnumerable<Finding> InitOrder(CSharpContext ctx) =>
            ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                .SelectMany(cls => InitOrder(ctx, cls));

        /// <summary>One finding per setup method of the class, when order evidently matters or other classes call it.</summary>
        static IEnumerable<Finding> InitOrder(CSharpContext ctx, ClassDeclarationSyntax cls)
        {
            if (cls.Modifiers.Any(SyntaxKind.StaticKeyword)) return Enumerable.Empty<Finding>();
            var publicMethods = cls.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword) && !m.Modifiers.Any(SyntaxKind.OverrideKeyword))
                .ToImmutableArray();
            var inits = publicMethods.Where(m => IsInitName(m.Identifier.Text, ctx.Settings.InitMethodPrefixes)).ToImmutableArray();
            if (inits.IsEmpty) return Enumerable.Empty<Finding>();
            var others = publicMethods.Except(inits).ToImmutableArray();
            if (others.IsEmpty) return Enumerable.Empty<Finding>();

            // Evidence that order matters: a state flag, or a guard that throws InvalidOperationException.
            var guards = cls.DescendantNodes().OfType<ThrowStatementSyntax>().Cast<SyntaxNode>()
                .Concat(cls.DescendantNodes().OfType<ThrowExpressionSyntax>())
                .Where(t => t.ToString().Contains("InvalidOperationException"))
                .Where(t => !inits.Any(i => i.Span.Contains(t.Span)))
                .ToImmutableArray();
            var flags = cls.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables)
                .Where(v => StateWords.Any(w => v.Identifier.Text.ToLowerInvariant().Contains(w)))
                .ToImmutableArray();
            var evidence = new Evidence(guards, flags, Text.Join(others.Select(o => o.Identifier.Text + "()").Distinct().Take(3).ToImmutableArray()));
            return inits.Select(init => SetupFirst(ctx, cls, init, evidence)).OfType<Finding>();
        }

        /// <summary>What a class shows about its setup order: runtime guards, state flags, and the methods that need setup first.</summary>
        sealed record Evidence(ImmutableArray<SyntaxNode> Guards, ImmutableArray<VariableDeclaratorSyntax> Flags, string OtherNames)
        {
            public bool Any => Guards.Length > 0 || Flags.Length > 0;
        }

        static Finding? SetupFirst(CSharpContext ctx, ClassDeclarationSyntax cls, MethodDeclarationSyntax init, Evidence evidence)
        {
            var symbol = ctx.Model(cls.SyntaxTree).GetDeclaredSymbol(init);
            var external = ctx.CallsTo(symbol)
                .Where(c => CSharpContext.TypeNameOf(c.Node) != CSharpContext.TypeNameOf(init))
                .ToImmutableArray();
            if (!evidence.Any && external.Length < 2) return null;

            var className = cls.Identifier.Text;
            var otherNames = evidence.OtherNames;
            var locs = evidence.Guards.Take(5).Select(g => ctx.Locate(g, "runtime guard"))
                .Concat(external.Select(c => ctx.Locate(c.Node, "caller")))
                .Prepend(ctx.Locate(init.Identifier, "setup method"))
                .ToImmutableArray();
            return new Finding
            {
                RuleId = "CNE01",
                Kind = Kind.Execution,
                Title = className + "." + init.Identifier.Text + "() must be called first",
                GroupKey = className + "." + init.Identifier.Text,
                Locations = locs,
                Degree = Math.Max(1, external.Length),
                Cause = "Callers must call " + init.Identifier.Text + "() before " + otherNames + ". " +
                        (external.Length == 1 ? "1 caller in another class has to remember this. " : external.Length > 1 ? external.Length + " callers in other classes each have to remember this. " : "") +
                        (evidence.Any ? "The class only finds out at runtime (" + (evidence.Flags.Length > 0 ? "it tracks a " + evidence.Flags[0].Identifier.Text + " flag" : "it throws InvalidOperationException") + ")."
                                      : "Nothing in the types stops a caller getting the order wrong."),
                Suggestion = "Make the wrong order impossible: do the setup in the constructor or a static factory (for example `" + className + ".Create" +
                             (init.Identifier.Text.StartsWith("Open", StringComparison.Ordinal) || init.Identifier.Text.StartsWith("Connect", StringComparison.Ordinal) ? "Connected" : "") +
                             "Async()`), or have " + init.Identifier.Text + "() return a different type that is the only one with " + otherNames + ".",
                ChangeProbe = "If setup gains another step, or more callers are added, each one must get the order right by hand. Is this class likely to be used in new places?",
            };
        }

        /// <summary>"Open", "OpenAsync" and "OpenConnection" are setup names for the prefix "Open"; "Opened" and "OpenAsyncly" are not.</summary>
        static bool IsInitName(string name, IReadOnlyList<string> prefixes) =>
            prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal) && IsInitSuffix(name.Substring(p.Length)));

        static bool IsInitSuffix(string rest) =>
            rest.Length == 0 || (char.IsUpper(rest[0]) && !rest.StartsWith("Async", StringComparison.Ordinal)) || rest == "Async";

        static IEnumerable<Finding> FixedWaits(CSharpContext ctx) =>
            Scoring.NumberRepeats(ctx.Roots.SelectMany(root => root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Select(inv => FixedWait(ctx, inv))
                .OfType<Finding>());

        static Finding? FixedWait(CSharpContext ctx, InvocationExpressionSyntax inv)
        {
            if (!(inv.Expression is MemberAccessExpressionSyntax ma)) return null;
            var target = ma.Expression.ToString();
            var name = ma.Name.Identifier.Text;
            bool sleep = target.EndsWith("Thread", StringComparison.Ordinal) && name == "Sleep";
            bool delay = target.EndsWith("Task", StringComparison.Ordinal) && name == "Delay";
            if (!sleep && !delay) return null;
            var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "";
            if (arg == "0" || arg.Contains("Infinite")) return null;

            var loc = ctx.Locate(inv, "wait");
            var inLoop = inv.Ancestors().Any(a => a is WhileStatementSyntax || a is ForStatementSyntax || a is DoStatementSyntax || a is ForEachStatementSyntax);
            return new Finding
            {
                RuleId = "CNT01",
                Kind = Kind.Timing,
                Title = (sleep ? "Thread.Sleep" : "Task.Delay") + "(" + Text.Quote(arg) + ")",
                GroupKey = loc.Anchor + "|" + inv.ToString(),
                Locations = ImmutableArray.Create(loc),
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
