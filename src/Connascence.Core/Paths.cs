using System.Collections.Immutable;
using System.Linq;
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
        public static readonly ImmutableArray<string> DefaultExcludes = ImmutableArray.Create(
            "**/bin/**", "**/obj/**", "**/*.g.cs", "**/*.generated.cs", "**/*.Designer.cs", "**/node_modules/**", "**/*.min.js",
            // Migrations are append-only history: copies between them never change together.
            "**/Migrations/**", "**/migrations/**", "**/db/migrate/**", "**/DbScripts*/**", "**/????-??-??_*.sql", "**/V*__*.sql");

        // Test folders and file-name conventions, in any case: tests/, Foo.Tests/, foo.test.js, test_foo.py, foo_test.go.
        // Also mocks, fakes and stubs folders (often shipped with an app for demos and offline use).
        static readonly Regex TestPath = new Regex(
            @"(^|/)(tests?|specs?|__tests__|testing)(/|$)|[.](Tests?|Specs?|IntegrationTests|UnitTests)(/|$)|[._-](test|spec)\.[a-z]+$|(^|/)test_[^/]*\.py$|_test\.(go|py|rb)$|(^|/)(mocks?|fakes?|stubs?)(/|$)",
            RegexOptions.IgnoreCase);

        // Test and fake classes by name, as whole words: OrderTests.cs, BasketMockService.cs, fake_clock.py.
        // Not Latest.cs, Contest.cs, Stubborn.cs or Hammock.cs.
        static readonly Regex TestName = new Regex(
            @"(^|/|[a-z0-9_])(Tests?|Specs?)\.cs$|(^|/)([A-Za-z0-9]*[a-z0-9])?(Mock|Fake|Stub)s?([A-Z0-9_][A-Za-z0-9_]*)?\.[a-z]+$|(^|/|[_-])(mock|fake|stub)s?([_-][a-z0-9_-]*)?\.[a-z]+$");

        static readonly ImmutableArray<Regex> DefaultExcludeRegexes = DefaultExcludes.Select(Glob.ToRegex).ToImmutableArray();

        /// <summary>A path relative to the repository (or, in the analyzer, to the folder above the project), with forward slashes.</summary>
        public static bool IsTest(string relativePath) =>
            Normalise(relativePath) is var p && (TestPath.IsMatch(p) || TestName.IsMatch(p));

        public static bool IsExcludedByDefault(string relativePath)
        {
            var p = Normalise(relativePath);
            return DefaultExcludeRegexes.Any(r => r.IsMatch(p));
        }

        static string Normalise(string? path) => (path ?? "").Replace('\\', '/');
    }

    /// <summary>Glob patterns for config and futures: **, * and ? on forward-slash paths, case-insensitive.</summary>
    public static class Glob
    {
        // A glob's parts: "**/" (any folders), "**" (anything), "*" (within one folder), "?" (one character), or plain text.
        static readonly Regex Parts = new Regex(@"\*\*/|\*\*|\*|\?|[^*?]+");

        /// <summary>Supports **, * and ? on forward-slash paths.</summary>
        public static Regex ToRegex(string glob)
        {
            var trimmed = glob.Replace('\\', '/').Trim();
            var g = trimmed.StartsWith("./", System.StringComparison.Ordinal) ? trimmed.Substring(2) : trimmed;
            var body = string.Concat(Parts.Matches(g).Cast<Match>().Select(m => m.Value switch
            {
                "**/" => "(?:.*/)?",
                "**" => ".*",
                "*" => "[^/]*",
                "?" => "[^/]",
                var text => Regex.Escape(text),
            }));
            // A glob without wildcards matches that path and everything under it, so a folder named like "Tally.Billing" works.
            var under = g.Contains("*") || g.Contains("?") ? "" : "(?:/.*)?";
            return new Regex("^" + body + under + "$", RegexOptions.IgnoreCase);
        }
    }
}
