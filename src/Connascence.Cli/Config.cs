using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connascence.Cli
{
    /// <summary>Contents of .connascence.json at the analysis root.</summary>
    public sealed class Config
    {
        /// <summary>auto: C# files get the full analysis, other languages get generic mode. generic: everything in generic mode. csharp: C# only.</summary>
        public string Mode { get; set; } = "auto";

        public bool IncludeTests { get; set; } = false;

        public List<string> Exclude { get; set; } = new List<string>
        {
            "**/bin/**", "**/obj/**", "**/*.g.cs", "**/*.generated.cs", "**/*.Designer.cs", "**/node_modules/**", "**/*.min.js",
            // Migrations are append-only history: copies between them never change together.
            "**/Migrations/**", "**/migrations/**", "**/db/migrate/**", "**/DbScripts*/**", "**/????-??-??_*.sql", "**/V*__*.sql",
        };

        public Thresholds Thresholds { get; set; } = new Thresholds();
        public VolatilityConfig Volatility { get; set; } = new VolatilityConfig();
        public LogConfig Log { get; set; } = new LogConfig();
        public Settings Scoring { get; set; } = new Settings();

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        public const string FileName = ".connascence.json";

        public static Config Load(string root)
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return new Config();
            try
            {
                var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
                c.Scoring ??= new Settings();
                c.Thresholds ??= new Thresholds();
                c.Volatility ??= new VolatilityConfig();
                c.Log ??= new LogConfig();
                return c;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not read " + path + ": " + ex.Message);
            }
        }
    }

    public sealed class Thresholds
    {
        /// <summary>check fails if the score is below this. 0 turns it off.</summary>
        public int MinScore { get; set; } = 0;

        /// <summary>check fails if any open finding costs at least this much.</summary>
        public double MaxFindingCost { get; set; } = 20;

        /// <summary>"new": only findings not in the baseline can fail a check. "all": any open finding can.</summary>
        public string FailOn { get; set; } = "new";
    }

    public sealed class VolatilityConfig
    {
        public bool Enabled { get; set; } = true;
        public int Months { get; set; } = 6;
        public int Medium { get; set; } = 3;
        public int High { get; set; } = 11;
    }

    public sealed class LogConfig
    {
        /// <summary>Append a line to .connascence/log/runs.jsonl on every analyze/check.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Findings at or above this cost are listed in each run's log line, so they can be tracked over time.</summary>
        public double TrackCost { get; set; } = 10;
    }
}
