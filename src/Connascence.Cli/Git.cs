using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Connascence.Cli
{
    /// <summary>Thin wrapper over the git command line. Everything here is optional: no git, no volatility.</summary>
    public sealed class Git
    {
        readonly string _top;       // repo top level, as git reports it
        readonly string _prefix;    // the analysis root, relative to the top level ("" or "src/app/")

        Git(string top, string prefix) { _top = top; _prefix = prefix; }

        public static Git Open(string root)
        {
            var top = Run(root, "rev-parse --show-toplevel");
            var prefix = Run(root, "rev-parse --show-prefix");
            return top == null || prefix == null ? null : new Git(top.Trim(), prefix.Trim());
        }

        // core.quotePath=false: git otherwise quotes names with non-ASCII characters ("Caf\303\251.cs"), which would never match.
        const string Plain = "-c core.quotePath=false ";

        static string Run(string dir, string args) => Exec(dir, args).Output;

        static (string Output, string Error) Exec(string dir, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", Plain + args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    var error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? (output, null) : (null, error.Trim());
                }
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        /// <summary>Runs git and returns its output, or throws: the gate must not pass because git couldn't say what changed.</summary>
        string Required(string args, string what)
        {
            var (output, error) = Exec(_top, args);
            if (output == null) throw new InvalidOperationException("couldn't list " + what + " with git: " + error);
            return output;
        }

        /// <summary>Repo-relative path to analysis-root-relative path, or null if outside the root.</summary>
        string ToRootRelative(string repoPath)
        {
            var path = repoPath.Replace('\\', '/');
            return _prefix.Length == 0 ? path : path.StartsWith(_prefix, StringComparison.Ordinal) ? path.Substring(_prefix.Length) : null;
        }

        /// <summary>NUL-separated output (-z) split into paths, made relative to the analysis root.</summary>
        List<string> Paths(string output) =>
            output.Split('\0').Where(l => l.Length > 0).Select(ToRootRelative).Where(p => p != null).ToList();

        public string Head() => Run(_top, "rev-parse --short HEAD")?.Trim();

        public string Top => _top;
        public string Prefix => _prefix;

        public List<string> StagedFiles() =>
            Paths(Required("diff --cached --name-only -z --diff-filter=ACMR", "the staged files"));

        public List<string> ChangedSince(string reference) =>
            Paths(Required("diff --name-only -z --diff-filter=ACMR " + reference, "the files changed since " + reference));

        /// <summary>Writes every staged file (the index, as it will be committed) under <paramref name="dir"/>.</summary>
        public void ExportIndex(string dir) =>
            Required("checkout-index -a -f --prefix=\"" + dir.TrimEnd('/', '\\') + "/\"", "the staged files");

        /// <summary>Files changed by each commit in the window, newest first.</summary>
        public List<List<string>> Commits(int months)
        {
            var output = Run(_top, "log --since=\"" + months + " months ago\" --no-merges --name-only --pretty=format:@@commit@@");
            var commits = new List<List<string>>();
            if (output == null) return commits;
            List<string> current = null;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line == "@@commit@@") { current = new List<string>(); commits.Add(current); continue; }
                if (line.Length == 0 || current == null) continue;
                var rel = ToRootRelative(line);
                if (rel != null) current.Add(rel);
            }
            return commits;
        }

        public static Dictionary<string, int> Churn(List<List<string>> commits)
        {
            var churn = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in commits)
                foreach (var f in c.Distinct())
                {
                    int n;
                    churn.TryGetValue(f, out n);
                    churn[f] = n + 1;
                }
            return churn;
        }
    }
}
