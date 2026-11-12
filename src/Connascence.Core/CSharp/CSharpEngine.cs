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
        public static IReadOnlyList<ICSharpDetector> Detectors() => new List<ICSharpDetector>
        {
            new MeaningDetector(),
            new PositionDetector(),
            new AlgorithmDetector(),
            new ExecutionDetector(),
            new StateDetector(),
        };

        public static IReadOnlyList<Finding> Analyze(CSharpContext ctx)
        {
            var findings = Detectors().SelectMany(d => Run(d, ctx)).ToImmutableArray();

            var lines = ctx.Files.GroupBy(f => f.RelativePath)
                .ToImmutableDictionary(g => g.Key, g => g.First().Tree.GetText().Lines.Select(l => l.ToString()).ToArray());
            Func<string, string[]?> linesOf = p => lines.TryGetValue(p, out var l) ? l : null;

            var scored = findings
                .Select(f => Scoring.Score(f with { Locations = Ordered(f.Locations) }, ctx.Settings))
                .Select(f => Accepting(f, linesOf));
            return Scoring.Consolidate(scored.ToImmutableArray()).OrderByDescending(f => f.Cost).ToImmutableArray();
        }

        /// <summary>One detector's findings. A detector that fails is reported as a warning, and the others still run.</summary>
        static ImmutableArray<Finding> Run(ICSharpDetector detector, CSharpContext ctx)
        {
            try { return detector.Detect(ctx).ToImmutableArray(); }
            catch (Exception ex)
            {
                ctx.Warnings.Add(detector.GetType().Name + " failed: " + ex.Message);
                return ImmutableArray<Finding>.Empty;
            }
        }

        /// <summary>Declarations first, then by file and line.</summary>
        static ImmutableArray<CodeLocation> Ordered(IReadOnlyList<CodeLocation> locations) =>
            locations.OrderBy(l => l.Role == "declaration" || l.Role == "definition" || l.Role == "setup method" ? 0 : 1)
                .ThenBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Line).ToImmutableArray();

        /// <summary>The finding, accepted if a "connascence: accept" comment marks one of its places.</summary>
        public static Finding Accepting(Finding f, Func<string, string[]?> linesOf) =>
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
            }).ToList();
            var compilation = CSharpCompilation.Create("ConnascenceAnalysis", files.Select(f => f.Tree), references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            return new CSharpContext(compilation, files, settings);
        }

        public static int CountCodeLines(string text)
        {
            int n = 0;
            bool inBlock = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (inBlock) { if (line.Contains("*/")) inBlock = false; continue; }
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.StartsWith("/*", StringComparison.Ordinal)) { if (!line.Contains("*/")) inBlock = true; continue; }
                if (line == "{" || line == "}") continue;
                n++;
            }
            return n;
        }
    }

    public sealed record SourceInput
    {
        public required string Path { get; init; }
        public required string RelativePath { get; init; }
        public required string Project { get; init; }
        public required string Text { get; init; }
    }
}
