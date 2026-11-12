using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Connascence;
using Connascence.CSharp;
using Connascence.Generic;
using Microsoft.CodeAnalysis;

// Each test feeds small sources through the engine and checks what is (and isn't) reported.
var tests = new List<(string Name, Action Body)>
{
    ("magic status code across projects is flagged with an enum suggestion", () =>
    {
        var f = Analyze(("A", "a.cs", "class Order { public int Status; } class S { void M(Order o) { if (o.Status == 3) {} } }"),
                        ("B", "b.cs", "class T { void N(Order o) { o.Status = 3; } }"));
        var m = Single(f, "CNM01");
        Assert(m.Distance == Distance.CrossProject, "distance should be cross-project");
        Assert(m.Suggestion.Contains("OrderStatus"), "suggestion should name OrderStatus: " + m.Suggestion);
    }),
    ("named constants are not magic values", () =>
    {
        var f = Analyze(("A", "a.cs", "class C { const int Shipped = 3; enum E { X = 3 } void M(int s) { if (s == Shipped) {} } }"));
        None(f, "CNM01");
    }),
    ("boolean literal to our own method is flagged, framework calls are not", () =>
    {
        var f = Analyze(("A", "a.cs", "using System.Threading.Tasks; class C { void Ship(int id, bool notify) {} async Task M() { Ship(1, true); Ship(2, false); await Task.Delay(1).ConfigureAwait(false); } }"));
        var b = Single(f, "CNM02");
        Assert(b.Degree == 2, "degree 2 expected, got " + b.Degree);
    }),
    ("named boolean argument is fine", () =>
    {
        var f = Analyze(("A", "a.cs", "class C { void Ship(int id, bool notify) {} void M() { Ship(1, notify: true); } }"));
        None(f, "CNM02");
    }),
    ("look-alike parameters by position", () =>
    {
        var f = Analyze(("A", "a.cs", "class C { void Pay(decimal net, decimal tax, decimal total, string cur) {} void M() { Pay(1m, 2m, 3m, \"NZD\"); } }"));
        Single(f, "CNP01");
    }),
    ("duplicate bodies fold the magic values inside them", () =>
    {
        const string body = "{ var t = x * 2m; var r = 0m; if (t > 500m) r = 0.05m; if (t > 900m) r = 0.1m; var d = t * r; System.Console.WriteLine(d); return d; }";
        var f = Analyze(("A", "a.cs", "class P { decimal D(decimal x) " + body + " }"),
                        ("B", "b.cs", "class Q { decimal E(decimal x) " + body + " }"));
        var copy = Single(f, "CNA01");
        Assert(copy.Subsumed.Count >= 2, "expected folded findings, got " + copy.Subsumed.Count);
        None(f, "CNV02");
    }),
    ("setup method with runtime guard", () =>
    {
        var f = Analyze(("A", "a.cs", "using System; class Client { bool _connected; public void Connect() { _connected = true; } public void Send() { if (!_connected) throw new InvalidOperationException(); } }"));
        Single(f, "CNE01");
    }),
    ("shared mutable static", () =>
    {
        var f = Analyze(("A", "a.cs", "using System.Collections.Generic; static class Cache { public static Dictionary<int,int> Items = new Dictionary<int,int>(); }"),
                        ("B", "b.cs", "class U { void A() { Cache.Items[1] = 1; } void B() { var x = Cache.Items[1]; } }"));
        Single(f, "CNI01");
    }),
    ("readonly immutable static is fine", () =>
    {
        var f = Analyze(("A", "a.cs", "static class K { public static readonly string Name = \"x\"; } class U { void A() { var a = K.Name; } void B() { var b = K.Name; } }"));
        None(f, "CNI01");
    }),
    ("same constant in two types", () =>
    {
        var f = Analyze(("A", "a.cs", "class A { public const int MaxNameLength = 50; } class B { public const int MaxNameLength = 50; }"));
        Single(f, "CNV01");
    }),
    ("different table names are not a duplicated constant", () =>
    {
        var f = Analyze(("A", "a.cs", "class A { const string TableName = \"orders\"; } class B { const string TableName = \"invoices\"; }"));
        None(f, "CNV01");
    }),
    ("inline acceptance marks a finding as accepted", () =>
    {
        var f = Analyze(("A", "a.cs", "class C {\n void M() {\n // connascence: accept - test fixture\n System.Threading.Thread.Sleep(10);\n }\n}"));
        var t = Single(f, "CNT01");
        Assert(t.Accepted && t.AcceptedReason.Contains("test fixture"), "should be accepted");
    }),
    ("fingerprint ignores line moves but changes when the coupling grows", () =>
    {
        var a = Single(Analyze(("A", "a.cs", "class O { public int Status; void M() { if (Status == 3) {} } void N() { Status = 3; } }")), "CNM01");
        var b = Single(Analyze(("A", "a.cs", "\n\n\nclass O { public int Status; void M() { if (Status == 3) {} } void N() { Status = 3; } }")), "CNM01");
        var c = Single(Analyze(("A", "a.cs", "class O { public int Status; void M() { if (Status == 3) {} } void N() { Status = 3; } void P() { if (Status != 3) {} } }")), "CNM01");
        Assert(a.Fingerprint == b.Fingerprint, "moving code should not change the id");
        Assert(a.Fingerprint != c.Fingerprint, "adding a place should change the id");
    }),
    ("cost follows strength x degree x distance", () =>
    {
        var s = new Settings();
        var f = Scoring.Score(new Finding { RuleId = "X", Kind = Kind.Meaning, Degree = 4, FixedDistance = Distance.CrossProject, Locations = new[] { new CodeLocation { File = "a", Project = "p" } } }, s);
        Assert(Math.Abs(f.Cost - 3 * 4 * 2.0) < 0.001, "cost was " + f.Cost);
    }),
    ("generic mode finds a repeated status code and a fixed sleep in Python", () =>
    {
        var files = new List<TextFile>
        {
            GenericEngine.Load("a.py", "p", "import time\ndef f(o):\n    if o.status == 3:\n        time.sleep(2)\n"),
            GenericEngine.Load("b.py", "p", "def g(o):\n    return o.status == 3\n"),
        };
        var f = GenericEngine.Analyze(files, new Settings());
        Single(f, "GNM01");
        Single(f, "GNT01");
    }),
};

int failed = 0;
foreach (var t in tests)
{
    try { t.Body(); Console.WriteLine("pass  " + t.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL  " + t.Name + "\n      " + ex.Message); }
}
Console.WriteLine();
Console.WriteLine(failed == 0 ? "All " + tests.Count + " tests passed." : failed + " of " + tests.Count + " tests failed.");
return failed == 0 ? 0 : 1;

static List<Finding> Analyze(params (string Project, string File, string Code)[] sources)
{
    var refs = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator)
        .Where(p => p.Length > 0).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
    var ctx = CSharpEngine.FromSources(sources.Select(s => new SourceInput { Path = s.File, RelativePath = s.Project + "/" + s.File, Project = s.Project, Text = s.Code }), refs, new Settings());
    var (findings, warnings) = CSharpEngine.Analyze(ctx);
    if (warnings.Count > 0) throw new Exception(string.Join("; ", warnings));
    return findings.ToList();
}

static Finding Single(IReadOnlyList<Finding> f, string rule)
{
    var hits = f.Where(x => x.RuleId == rule).ToList();
    if (hits.Count != 1) throw new Exception("expected one " + rule + ", got " + hits.Count + ". All: " + string.Join(", ", f.Select(x => x.RuleId + " " + x.Title)));
    return hits[0];
}

static void None(IReadOnlyList<Finding> f, string rule)
{
    var hits = f.Where(x => x.RuleId == rule).ToList();
    if (hits.Count != 0) throw new Exception("expected no " + rule + ", got: " + string.Join(", ", hits.Select(x => x.Title)));
}

static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
