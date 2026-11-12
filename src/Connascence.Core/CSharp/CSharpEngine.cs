using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Connascence.CSharp
{
    /// <summary>Runs every C# detector over a compilation and scores the results.</summary>
    public static class CSharpEngine
    {
        public static IReadOnlyList<ICSharpDetector> Detectors() => ImmutableArray.Create<ICSharpDetector>(
            new MeaningDetector(),
            new PositionDetector(),
            new AlgorithmDetector(),
            new ExecutionDetector(),
            new StateDetector());

        /// <summary>Every detector's findings, scored, plus a warning for each detector that failed.</summary>
        public static (IReadOnlyList<Finding> Findings, IReadOnlyList<string> Warnings) Analyze(CSharpContext ctx)
        {
            var runs = Detectors().Select(d => Run(d, ctx)).ToImmutableArray();
            var findings = runs.SelectMany(r => r.Findings).ToImmutableArray();

            var lines = ctx.Files.GroupBy(f => f.RelativePath)
                .ToImmutableDictionary(g => g.Key, g => g.First().Tree.GetText().Lines.Select(l => l.ToString()).ToArray());
            Func<string, IReadOnlyList<string>?> linesOf = p => lines.TryGetValue(p, out var l) ? l : null;

            var scored = findings
                .Select(f => Scoring.Score(f with { Locations = Ordered(f.Locations) }, ctx.Settings))
                .Select(f => Accepting(f, linesOf));
            var warnings = runs.Select(r => r.Warning).OfType<string>().ToImmutableArray();
            return (Scoring.Consolidate(scored.ToImmutableArray()).OrderByDescending(f => f.Cost).ToImmutableArray(), warnings);
        }

        /// <summary>One detector's findings. A detector that fails gives a warning instead, and the others still run.</summary>
        static (ImmutableArray<Finding> Findings, string? Warning) Run(ICSharpDetector detector, CSharpContext ctx)
        {
            try { return (detector.Detect(ctx).ToImmutableArray(), null); }
            catch (Exception ex) { return (ImmutableArray<Finding>.Empty, detector.GetType().Name + " failed: " + ex.Message); }
        }

        /// <summary>Declarations first, then by file and line.</summary>
        static ImmutableArray<CodeLocation> Ordered(IReadOnlyList<CodeLocation> locations) =>
            locations.OrderBy(l => l.Role == Roles.Declaration || l.Role == Roles.Definition || l.Role == Roles.SetupMethod ? 0 : 1)
                .ThenBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Line).ToImmutableArray();

        /// <summary>The finding, accepted if a "connascence: accept" comment marks one of its places.</summary>
        public static Finding Accepting(Finding f, Func<string, IReadOnlyList<string>?> linesOf) =>
            InlineAcceptance.ReasonFor(linesOf, f) is { } reason ? f with { Accepted = true, AcceptedReason = "inline: " + reason } : f;

        /// <summary>Builds one compilation from all files so calls across projects resolve.</summary>
        public static CSharpContext FromSources(IEnumerable<SourceInput> inputs, IEnumerable<MetadataReference> references, Settings? settings)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var files = inputs.Select(i => new SourceFile
            {
                Tree = CSharpSyntaxTree.ParseText(i.Text, options, i.Path),
                RelativePath = i.RelativePath,
                Project = i.Project,
            }).ToImmutableArray();
            var compilation = CSharpCompilation.Create("ConnascenceAnalysis", files.Select(f => f.Tree), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            return new CSharpContext(compilation, files, settings);
        }

        /// <summary>Lines with code on them: not blank, not comments, not a lone brace.</summary>
        public static int CountCodeLines(string text) =>
            text.Split('\n').Select(raw => raw.Trim()).Where(line => line.Length > 0)
                .Aggregate((Count: 0, InComment: false), (s, line) =>
                    s.InComment ? (s.Count, !line.Contains("*/"))
                    : line.StartsWith("//", StringComparison.Ordinal) ? s
                    : line.StartsWith("/*", StringComparison.Ordinal) ? (s.Count, !line.Contains("*/"))
                    : line == "{" || line == "}" ? s
                    : (s.Count + 1, false))
                .Count;
    }

    public sealed record SourceInput
    {
        public required string Path { get; init; }
        public required string RelativePath { get; init; }
        public required string Project { get; init; }
        public required string Text { get; init; }
    }
}
