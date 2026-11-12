using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Connascence.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Connascence.Analyzers
{
    /// <summary>
    /// Reports connascence findings as build diagnostics. Runs once per project at the end of compilation,
    /// because degree and distance need the whole project. That means the warnings appear on build,
    /// not live as you type.
    ///
    /// Settings (.editorconfig or .globalconfig):
    ///   connascence.min_cost = 10          only report findings costing at least this much
    ///   connascence.include_tests = true   also analyse tests, mocks, fakes and stubs (skipped by default, as in the CLI)
    /// Generated code and migrations are always skipped, using the same rules as the CLI (Paths).
    ///   dotnet_diagnostic.CNM01.severity = none | suggestion | warning | error
    /// Accept a finding inline with a comment on (or just above) a flagged line:
    ///   // connascence: accept - format fixed by the bank's spec, won't change
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ConnascenceAnalyzer : DiagnosticAnalyzer
    {
        const string Category = "Connascence";

        static readonly Dictionary<string, DiagnosticDescriptor> Rules = new Dictionary<string, DiagnosticDescriptor>
        {
            ["CNM01"] = Rule("CNM01", "Magic value shared by several places", Kind.Meaning),
            ["CNM02"] = Rule("CNM02", "Boolean flag argument", Kind.Meaning),
            ["CNP01"] = Rule("CNP01", "Look-alike parameters passed by position", Kind.Position),
            ["CNP02"] = Rule("CNP02", "Tuple unpacked by position", Kind.Position),
            ["CNP03"] = Rule("CNP03", "Fields read by index from a split string", Kind.Position),
            ["CNA01"] = Rule("CNA01", "Same logic copied", Kind.Algorithm),
            ["CNA02"] = Rule("CNA02", "Format or pattern written out more than once", Kind.Algorithm),
            ["CNA03"] = Rule("CNA03", "Crypto or hashing set up independently", Kind.Algorithm),
            ["CNE01"] = Rule("CNE01", "Method must be called before others", Kind.Execution),
            ["CNT01"] = Rule("CNT01", "Fixed wait", Kind.Timing),
            ["CNV01"] = Rule("CNV01", "Constant defined more than once", Kind.Value),
            ["CNV02"] = Rule("CNV02", "Limit checked in several places", Kind.Value),
            ["CNI01"] = Rule("CNI01", "Shared mutable static state", Kind.Identity),
        };

        static DiagnosticDescriptor Rule(string id, string title, Kind kind)
        {
            var entry = Catalog.Of(kind);
            return new DiagnosticDescriptor(id, title, "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
                description: "Connascence of " + kind.ToString().ToLowerInvariant() + ". " + entry.Means + " " + entry.Weaken,
                customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
        }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Rules.Values.ToImmutableArray();

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(start =>
            {
                start.RegisterCompilationEndAction(end => Report(end));
            });
        }

        static void Report(CompilationAnalysisContext end)
        {
            var compilation = end.Compilation;
            var options = end.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            double minCost = 10;
            string raw;
            if (options.TryGetValue("connascence.min_cost", out raw))
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out minCost);

            bool includeTests = options.TryGetValue("connascence.include_tests", out raw) && raw.Trim().ToLowerInvariant() == "true";
            string projectDir;
            options.TryGetValue("build_property.ProjectDir", out projectDir);
            var trees = compilation.SyntaxTrees.Where(t =>
            {
                var rel = RelativeToProjectParent(t.FilePath, projectDir);
                return !Paths.IsExcludedByDefault(rel) && (includeTests || !Paths.IsTest(rel));
            }).ToList();
            var files = trees.Select(t => new SourceFile { Tree = t, RelativePath = t.FilePath, Project = compilation.AssemblyName ?? "" }).ToList();
            var ctx = new CSharpContext(compilation, files, new Settings());
            var byPath = trees.GroupBy(t => t.FilePath).ToDictionary(g => g.Key, g => g.First());

            foreach (var f in CSharpEngine.Analyze(ctx).Findings)
            {
                end.CancellationToken.ThrowIfCancellationRequested();
                DiagnosticDescriptor rule;
                if (f.Accepted || f.Cost < minCost || !Rules.TryGetValue(f.RuleId, out rule)) continue;
                var locations = f.Locations.Select(l => ToLocation(byPath, l)).Where(l => l != null).ToList();
                if (locations.Count == 0) continue;
                var message = f.Title + ". " + f.Cause + " Fix: " + f.Suggestion + " Ask: " + f.ChangeProbe +
                              " (cost " + f.Cost.ToString("0.#", CultureInfo.InvariantCulture) + ", id " + f.Fingerprint + ")";
                var props = ImmutableDictionary<string, string>.Empty
                    .Add("fingerprint", f.Fingerprint)
                    .Add("cost", f.Cost.ToString(CultureInfo.InvariantCulture));
                end.ReportDiagnostic(Diagnostic.Create(rule, locations[0], locations.Skip(1).Take(20), props, message));
            }
        }

        /// <summary>
        /// The file's path from the folder above the project ("Tally.Api/Migrations/Initial.cs"), so the shared path rules
        /// see the project's own name (MyApp.Tests) but not whatever folders the repository happens to sit in.
        /// </summary>
        static string RelativeToProjectParent(string path, string projectDir)
        {
            var p = (path ?? "").Replace('\\', '/');
            var dir = (projectDir ?? "").Replace('\\', '/').TrimEnd('/');
            if (dir.Length == 0) return System.IO.Path.GetFileName(p);
            var parent = dir.Contains("/") ? dir.Substring(0, dir.LastIndexOf('/') + 1) : "";
            return parent.Length > 0 && p.StartsWith(parent, System.StringComparison.OrdinalIgnoreCase) ? p.Substring(parent.Length) : System.IO.Path.GetFileName(p);
        }

        static Location ToLocation(Dictionary<string, SyntaxTree> byPath, CodeLocation l)
        {
            SyntaxTree tree;
            if (l.File == null || !byPath.TryGetValue(l.File, out tree)) return null;
            var text = tree.GetText();
            if (l.Line < 1 || l.Line > text.Lines.Count) return null;
            var line = text.Lines[l.Line - 1];
            var start = System.Math.Min(line.Start + System.Math.Max(0, l.Column - 1), line.End);
            return Location.Create(tree, TextSpan.FromBounds(start, line.End));
        }
    }
}
