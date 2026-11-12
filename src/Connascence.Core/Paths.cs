using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Connascence
{
    /// <summary>
    /// Which files are analysed. Shared by the CLI and the Roslyn analyzer, so a build warning and the report
    /// agree on what counts as test code, generated code and migrations.
    /// </summary>
    public static class Paths
    {
        /// <summary>Skipped unless .connascence.json says otherwise (the CLI) or always (the analyzer).</summary>
        public static readonly IReadOnlyList<string> DefaultExcludes = new[]
        {
            "**/bin/**", "**/obj/**", "**/*.g.cs", "**/*.generated.cs", "**/*.Designer.cs", "**/node_modules/**", "**/*.min.js",
            // Migrations are append-only history: copies between them never change together.
            "**/Migrations/**", "**/migrations/**", "**/db/migrate/**", "**/DbScripts*/**", "**/????-??-??_*.sql", "**/V*__*.sql",
        };

        // Tests, specs, and mocks, fakes and stubs (often shipped with an app for demos and offline use).
        static readonly Regex TestPath = new Regex(
            @"(^|/)(tests?|specs?|__tests__|testing)(/|$)|[.](Tests?|Specs?|IntegrationTests|UnitTests)(/|$)|(Tests?|Specs?)\.cs$|[._-](test|spec)\.[a-z]+$|(^|/)test_[^/]*\.py$|_test\.(go|py|rb)$|(^|/)[A-Za-z0-9]*(Mock|Fake|Stub)[A-Za-z0-9]*\.[a-z]+$|(^|/)(mocks?|fakes?|stubs?)(/|$)",
            RegexOptions.IgnoreCase);

        static readonly List<Regex> DefaultExcludeRegexes = DefaultExcludes.Select(Glob.ToRegex).ToList();

        /// <summary>A path relative to the repository (or, in the analyzer, to the folder above the project), with forward slashes.</summary>
        public static bool IsTest(string relativePath) => TestPath.IsMatch(Normalise(relativePath));

        public static bool IsExcludedByDefault(string relativePath)
        {
            var p = Normalise(relativePath);
            return DefaultExcludeRegexes.Any(r => r.IsMatch(p));
        }

        static string Normalise(string path) => (path ?? "").Replace('\\', '/');
    }

    /// <summary>Glob patterns for config and futures: **, * and ? on forward-slash paths, case-insensitive.</summary>
    public static class Glob
    {
        /// <summary>Supports **, * and ? on forward-slash paths.</summary>
        public static Regex ToRegex(string glob)
        {
            var g = glob.Replace('\\', '/').Trim();
            if (g.StartsWith("./")) g = g.Substring(2);
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < g.Length; i++)
            {
                var c = g[i];
                if (c == '*')
                {
                    if (i + 1 < g.Length && g[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < g.Length && g[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            // A glob without wildcards that names a folder matches everything under it.
            if (!g.Contains("*") && !g.Contains("?") && !Path.HasExtension(g)) sb.Append("(?:/.*)?");
            sb.Append("$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }
    }
}
