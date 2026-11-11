using System;
using System.Globalization;

namespace Tally.Billing
{
    public class StatementLine
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Payee { get; set; }
        public string Reference { get; set; }
    }

    // Bank file formats. The direct-credit layout is fixed by the bank's published spec.
    public static class BankFile
    {
        public static StatementLine ParseStatementLine(string line)
        {
            var parts = line.Split(',');
            return new StatementLine
            {
                Date = DateTime.ParseExact(parts[0], "dd/MM/yyyy", CultureInfo.InvariantCulture),
                Amount = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                Payee = parts[2].Trim(),
                Reference = parts[4].Trim(),
            };
        }

        public static string DirectCreditLine(string account, decimal amount, string payee, string reference)
        {
            var cents = ((long)(amount * 100)).ToString(CultureInfo.InvariantCulture);
            return "2," + account.PadRight(16) + ",50," + cents.PadLeft(10, '0') + "," + payee.PadRight(20) + "," + reference.PadRight(12);
        }

        public static bool IsHeader(string line)
        {
            var fields = line.Split(',');
            return fields[0] == "1" && fields[3] == "BNZ";
        }
    }
}
