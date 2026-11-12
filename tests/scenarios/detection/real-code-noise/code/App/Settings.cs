namespace App
{
    // Seen in dotnet/eShop: statics that are set once at declaration and only ever read.
    public static class Telemetry
    {
        public static string SourceName = "Orders";
        public static int Counter;
    }
    public class Tracing
    {
        public string Name() => Telemetry.SourceName;
        public string Again() => Telemetry.SourceName + "/v1";
        public void Hit() { Telemetry.Counter++; }
        public int Hits() => Telemetry.Counter;
    }
}
