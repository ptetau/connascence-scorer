using System.Collections.Generic;
namespace Core
{
    public static class Session
    {
        public static string CurrentUser;
        public static readonly Dictionary<string, int> Counters = new Dictionary<string, int>();
        public static readonly string Product = "core";
    }
    public class Audit
    {
        public void Log(string what) { Session.Counters[what] = 1; System.Console.WriteLine(Session.CurrentUser + what); }
    }
}
