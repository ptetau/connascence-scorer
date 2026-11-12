using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Connascence.CSharp;
using Connascence.Generic;
using Microsoft.CodeAnalysis;

namespace Connascence.Cli
{
    public sealed class AnalysisResult
    {
        public string Root { get; set; }
        public string Mode { get; set; }
        public List<Finding> Findings { get; set; } = new List<Finding>();
        public ScoreSummary Summary { get; set; }
        public int CSharpFiles { get; set; }
        public int GenericFiles { get; set; }
        public List<string> GenericLanguages { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Files { get; set; } = new List<string>();
        public List<Future> Futures { get; set; } = new List<Future>();
        public bool VolatilityUsed { get; set; }
        public string Commit { get; set; }
    }

    /// <summary>Finds the files, runs the right engine for each language, and applies volatility, futures and acceptances.</summary>
    public static class Workspace
    {
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".connascence", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "target", ".vs", ".idea", ".vscode",
            "vendor", "venv", ".venv", "__pycache__", ".next", ".nuxt", "coverage", "TestResults",
        };

        static readonly string[] ProjectMarkers =
        {
            "*.csproj", "*.fsproj", "*.vbproj", "package.json", "pyproject.toml", "setup.py", "go.mod", "Cargo.toml", "pom.xml",
            "build.gradle", "build.gradle.kts", "Gemfile", "composer.json", "Package.swift", "mix.exs",
        };

        public static AnalysisResult Analyze(string root, Config config, string modeOverride, bool useGit)
        {
            root = Path.GetFullPath(root);
            var mode = (modeOverride ?? config.Mode ?? "auto").ToLowerInvariant();
            var excludes = config.Exclude.Select(Glob.ToRegex).ToList();
            var settings = config.Scoring;
            var result = new AnalysisResult { Root = root, Mode = mode };

            var csharp = new List<SourceInput>();
            var generic = new List<TextFile>();
            int lines = 0;
            var projectCache = new Dictionary<string, string>();

            foreach (var path in Walk(root))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (excludes.Any(r => r.IsMatch(rel))) continue;
                if (!config.IncludeTests && Paths.IsTest(rel)) continue;
                var ext = Path.GetExtension(path);
                bool isCs = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase);
                if (!GenericEngine.SourceExtensions.Contains(ext)) continue;
                if (mode == "csharp" && !isCs) continue;

                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                if (text.Length > 2_000_000) { result.Warnings.Add("Skipped very large file " + rel); continue; }
                var project = ProjectOf(root, Path.GetDirectoryName(path), projectCache);
                result.Files.Add(rel);

                if (isCs && mode != "generic")
                {
                    csharp.Add(new SourceInput { Path = path, RelativePath = rel, Project = project, Text = text });
                    lines += CSharpEngine.CountCodeLines(text);
                }
                else
                {
                    var tf = GenericEngine.Load(rel, project, text);
                    generic.Add(tf);
                    lines += tf.Lines.Count(l => l.Trim().Length > 0 && !l.Trim().StartsWith("//") && !l.Trim().StartsWith("#"));
                    var lang = ext.TrimStart('.').ToLowerInvariant();
                    if (!result.GenericLanguages.Contains(lang)) result.GenericLanguages.Add(lang);
                }
            }

            if (csharp.Count > 0)
            {
                var ctx = CSharpEngine.FromSources(csharp, References(), settings);
                result.Findings.AddRange(CSharpEngine.Analyze(ctx));
                result.Warnings.AddRange(ctx.Warnings);
            }
            if (generic.Count > 0) result.Findings.AddRange(GenericEngine.Analyze(generic, settings));
            if (csharp.Count > 0 && generic.Count > 0) result.Findings = CrossLanguage.Merge(result.Findings, settings);
            result.CSharpFiles = csharp.Count;
            result.GenericFiles = generic.Count;

            var store = new Store(root);
            store.Apply(result.Findings);
            result.Futures = store.Futures().Futures;

            var git = useGit && config.Volatility.Enabled ? Git.Open(root) : null;
            if (git != null)
            {
                result.Commit = git.Head();
                var churn = Git.Churn(git.Commits(config.Volatility.Months));
                result.VolatilityUsed = churn.Count > 0;
                foreach (var f in result.Findings)
                {
                    int max = 0, n;
                    foreach (var file in f.Files) if (churn.TryGetValue(file, out n) && n > max) max = n;
                    f.Volatility = max;
                }
            }

            var likelihood = result.Futures.ToDictionary(x => x.Id ?? "", x => (x.Likelihood ?? "medium").ToLowerInvariant());
            foreach (var f in result.Findings)
            {
                f.VolatilityLevel = !result.VolatilityUsed ? null
                    : f.Volatility >= config.Volatility.High ? "high"
                    : f.Volatility >= config.Volatility.Medium ? "medium" : "low";
                double vBoost = f.VolatilityLevel == "high" ? 1.0 : f.VolatilityLevel == "medium" ? 0.5 : 0;
                double futureBoost = 1;
                foreach (var id in f.Futures)
                {
                    string l;
                    likelihood.TryGetValue(id, out l);
                    var b = l == "high" ? 2.0 : l == "low" ? 1.2 : 1.5;
                    if (b > futureBoost) futureBoost = b;
                }
                var stableFactor = f.StableReason != null && f.Futures.Count == 0 ? 0.25 : 1.0;
                f.Priority = Math.Round(f.Cost * (1 + vBoost) * futureBoost * stableFactor, 1);
            }

            result.Findings = result.Findings.OrderByDescending(f => f.Priority).ThenByDescending(f => f.Cost).ToList();
            result.Summary = Scoring.Summarise(result.Findings, lines, result.Files.Count, settings);
            return result;
        }

        static IEnumerable<string> Walk(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files, dirs;
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal)) yield return f;
                foreach (var d in dirs.OrderByDescending(x => x, StringComparer.Ordinal))
                    if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
            }
        }

        /// <summary>The nearest folder with a project file names the project. Otherwise the top-level folder.</summary>
        static string ProjectOf(string root, string dir, Dictionary<string, string> cache)
        {
            string cached;
            if (cache.TryGetValue(dir, out cached)) return cached;
            string result = null;
            var d = dir;
            while (d != null && d.Length >= root.Length)
            {
                foreach (var marker in ProjectMarkers)
                {
                    var hit = Directory.GetFiles(d, marker).FirstOrDefault();
                    if (hit != null)
                    {
                        result = marker.StartsWith("*") ? Path.GetFileNameWithoutExtension(hit) : Path.GetRelativePath(root, d).Replace('\\', '/');
                        if (result == ".") result = Path.GetFileName(root);
                        break;
                    }
                }
                if (result != null) break;
                d = Path.GetDirectoryName(d);
            }
            if (result == null)
            {
                var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
                result = rel == "." ? Path.GetFileName(root) : rel.Split('/')[0];
            }
            cache[dir] = result;
            return result;
        }

        static List<MetadataReference> _refs;

        static List<MetadataReference> References()
        {
            if (_refs != null) return _refs;
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator);
            _refs = tpa.Where(p => p.Length > 0 && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            return _refs;
        }
    }
}
