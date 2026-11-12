using System;
using System.Globalization;
using System.Text.RegularExpressions;
namespace App
{
    public class Writer
    {
        public string Stamp(DateTime d) => d.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        public bool ValidSku(string s) => Regex.IsMatch(s, @"^[A-Z]{3}-\d{4}$");
    }
    public class Reader
    {
        public DateTime Parse(string s) => DateTime.ParseExact(s, "yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        public bool LooksLikeSku(string s) => Regex.IsMatch(s, @"^[A-Z]{3}-\d{4}$");
        public string Label(DateTime d) => d.ToString("D", CultureInfo.InvariantCulture);
    }
}
