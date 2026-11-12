using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Connascence
{
    /// <summary>Small helpers for writing plain-English causes and suggestions.</summary>
    public static class Text
    {
        /// <summary>FNV-1a hash. Unlike string.GetHashCode it is the same in every process, so ids stay stable.</summary>
        public static string StableHash(string? s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in s ?? "") { h ^= c; h *= 16777619; }
                return h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        public static string Places(int n) => n == 1 ? "1 place" : n + " places";

        /// <summary>"1 caller passes" / "3 callers pass"</summary>
        public static string CountVerb(int n, string noun, string verbSingular, string verbPlural) =>
            Count(n, noun) + " " + (n == 1 ? verbSingular : verbPlural);

        /// <summary>"the 1 caller" / "both callers" / "all 5 callers"</summary>
        public static string All(int n, string singular, string? plural = null) =>
            n == 1 ? "the " + singular : n == 2 ? "both " + (plural ?? singular + "s") : "all " + n + " " + (plural ?? singular + "s");

        public static string Count(int n, string singular, string? plural = null) =>
            n + " " + (n == 1 ? singular : (plural ?? singular + "s"));

        /// <summary>"in 4 places across 3 files" / "in 2 places in one method" etc.</summary>
        public static string Spread(IReadOnlyList<CodeLocation> locs)
        {
            var files = locs.Select(l => l.File).Distinct().Count();
            var projects = locs.Select(l => l.Project).Distinct().Count();
            var where = projects > 1 ? "across " + projects + " projects"
                : files > 1 ? "across " + files + " files"
                : locs.All(l => l.Type == null) ? "in one file"
                : locs.Select(l => l.Type).Distinct().Count() > 1 ? "across " + locs.Select(l => l.Type).Distinct().Count() + " types in one file"
                : locs.Select(l => l.Member).Distinct().Count() > 1 ? "in one class"
                : "in one member";
            return "in " + Places(locs.Count) + " " + where;
        }

        /// <summary>"A.Foo, B.Bar and 3 more"</summary>
        public static string Members(IEnumerable<CodeLocation> locs, int max = 3)
        {
            var names = locs.Select(l => l.Type == null ? l.File : (l.Member == null ? l.Type : l.Type + "." + l.Member)).Distinct().ToList();
            if (names.Count <= max) return Join(names);
            return string.Join(", ", names.Take(max).ToArray()) + " and " + (names.Count - max) + " more";
        }

        public static string Join(IReadOnlyList<string> items)
        {
            if (items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            return string.Join(", ", items.Take(items.Count - 1).ToArray()) + " and " + items[items.Count - 1];
        }

        public static string Quote(string? literal)
        {
            if (literal == null) return "";
            if (literal.Length > 40) literal = literal.Substring(0, 37) + "...";
            return literal;
        }

        /// <summary>Splits "MaxNameLength", "MAX_NAME_LENGTH", "maxNameLength" into lower-case words.</summary>
        public static List<string> Words(string? identifier)
        {
            var spaced = Regex.Replace(identifier ?? "", "([a-z0-9])([A-Z])", "$1 $2");
            spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            return spaced.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant()).ToList();
        }

        public static string PascalCase(string? s)
        {
            var words = Words(Regex.Replace(s ?? "", "[^A-Za-z0-9_ ]", " "));
            var result = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)).ToArray());
            if (result.Length == 0 || char.IsDigit(result[0])) result = "Value" + result;
            return result;
        }
    }

    /// <summary>
    /// Inline acceptance: a comment containing "connascence: accept" on a flagged line,
    /// or the line above it, marks the finding as a deliberate choice. The rest of the
    /// comment is the reason.
    /// </summary>
    public static class InlineAcceptance
    {
        static readonly Regex Marker = new Regex(@"connascence:\s*accept\b[:\s-]*(?<reason>.*)$", RegexOptions.IgnoreCase);

        public static string? ReasonFor(Func<string, string[]?> linesOf, Finding f)
        {
            foreach (var loc in f.Locations)
            {
                var lines = linesOf(loc.File);
                if (lines == null) continue;
                for (int i = loc.Line - 1; i >= Math.Max(0, loc.Line - 2); i--)
                {
                    if (i >= lines.Length) continue;
                    var m = Marker.Match(lines[i]);
                    if (m.Success)
                    {
                        var reason = m.Groups["reason"].Value.Trim().TrimEnd('*', '/', ' ', '-', '#').Trim();
                        return reason.Length == 0 ? "(no reason given)" : reason;
                    }
                }
            }
            return null;
        }
    }
}
