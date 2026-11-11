using System.Collections.Generic;

namespace Tally.Domain
{
    public class Customer
    {
        public const int MaxNameLength = 80;
        const string TableName = "customers";

        public static readonly IReadOnlyList<string> Currencies = new[] { "NZD" };

        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Role { get; set; }
        public int PaymentTermsDays { get; set; } = 20;

        public string Table() => TableName;

        public string DisplayName()
        {
            var name = Name ?? "";
            return name.Length <= MaxNameLength ? name : name.Substring(0, MaxNameLength);
        }
    }
}
