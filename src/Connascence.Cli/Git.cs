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
        readonly string _root;      // analysis root
        readonly string _top;       // repo top level

        Git(string root, string top) { _root = root; _top = top; }

        public static Git Open(string root)
        {
            var top = Run(root, "rev-parse --show-toplevel");
            return top == null ? null : new Git(root, top.Trim());
        }

        static string Run(string dir, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? output : null;
                }
            }
            catch { return null; }
        }

        /// <summary>Repo-relative path to analysis-root-relative path, or null if outside the root.</summary>
        string ToRootRelative(string repoPath)
        {
            var full = Path.GetFullPath(Path.Combine(_top, repoPath));
            var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
            return rel.StartsWith("..") ? null : rel;
        }

        public string Head() => Run(_root, "rev-parse --short HEAD")?.Trim();

        public List<string> StagedFiles()
        {
            var output = Run(_top, "diff --cached --name-only --diff-filter=ACMR") ?? "";
            return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(ToRootRelative).Where(p => p != null).ToList();
        }

        public List<string> ChangedSince(string reference)
        {
            var output = Run(_top, "diff --name-only --diff-filter=ACMR " + reference) ?? "";
            return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(ToRootRelative).Where(p => p != null).ToList();
        }

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
