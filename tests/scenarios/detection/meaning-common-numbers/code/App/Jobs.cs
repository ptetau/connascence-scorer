using System.Collections.Generic;
namespace App
{
    public class Job { public int Mode { get; set; } public int Attempts { get; set; } public List<string> Items { get; set; } = new List<string>(); }
    public class Runner
    {
        public bool IsDryRun(Job j) => j.Mode == 2;
        public void DryRun(Job j) { j.Mode = 2; }
        public bool FirstTry(Job j) => j.Attempts == 1;
        public bool Retried(Job j) => j.Attempts == 1 || j.Attempts == 2;
        public bool Empty(Job j) => j.Items.Count == 0;
        public bool Single(Job j) => j.Items.Count == 1;
    }
    public class Report
    {
        public string Label(Job j) => j.Mode == 2 ? "dry run" : "live";
        public bool Any(Job j) => j.Items.Count == 0;
    }
}
