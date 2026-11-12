using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Connascence.CSharp
{
    /// <summary>Runs every C# detector over a compilation and scores the results.</summary>
    public static class CSharpEngine
    {
        public static IList<ICSharpDetector> Detectors() => new List<ICSharpDetector>
        {
            new MeaningDetector(),
            new PositionDetector(),
            new AlgorithmDetector(),
            new ExecutionDetector(),
            new StateDetector(),
        };

        public static List<Finding> Analyze(CSharpContext ctx)
        {
            var findings = new List<Finding>();
            foreach (var d in Detectors())
            {
                try { findings.AddRange(d.Detect(ctx)); }
                catch (Exception ex) { ctx.Warnings.Add(d.GetType().Name + " failed: " + ex.Message); }
            }

            var lines = ctx.Files.ToDictionary(f => f.RelativePath, f => f.Tree.GetText().Lines.Select(l => l.ToString()).ToArray());
            Func<string, string[]> linesOf = p => { string[] l; return lines.TryGetValue(p, out l) ? l : null; };

            foreach (var f in findings)
            {
                f.Locations = f.Locations.OrderBy(l => l.Role == Roles.Declaration || l.Role == Roles.Definition || l.Role == Roles.SetupMethod ? 0 : 1)
                    .ThenBy(l => l.File, StringComparer.Ordinal).ThenBy(l => l.Line).ToList();
                Scoring.Score(f, ctx.Settings);
                var reason = InlineAcceptance.ReasonFor(linesOf, f);
                if (reason != null) { f.Accepted = true; f.AcceptedReason = "inline: " + reason; }
            }
            return Scoring.Consolidate(findings).OrderByDescending(f => f.Cost).ToList();
        }

        /// <summary>Builds one compilation from all files so calls across projects resolve.</summary>
        public static CSharpContext FromSources(IEnumerable<SourceInput> inputs, IEnumerable<MetadataReference> references, Settings settings)
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
                if (line.StartsWith("//")) continue;
                if (line.StartsWith("/*")) { if (!line.Contains("*/")) inBlock = true; continue; }
                if (line == "{" || line == "}") continue;
                n++;
            }
            return n;
        }
    }

    public sealed class SourceInput
    {
        public string Path { get; set; }
        public string RelativePath { get; set; }
        public string Project { get; set; }
        public string Text { get; set; }
    }
}
