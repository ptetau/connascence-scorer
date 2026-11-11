using System;
namespace Reports
{
    public class Statement
    {
        // Exact copy of Pricing.Commission.
        public decimal CommissionFor(decimal sale)
        {
            var rate = 0.04m;
            if (sale > 5000m) rate = 0.06m;
            if (sale > 20000m) rate = 0.08m;
            var amount = sale * rate;
            amount = Math.Round(amount, 2);
            return amount;
        }

        // Pricing.Bonus with the variables renamed: still the same rule.
        public decimal BonusFor(decimal goal, decimal achieved)
        {
            var r = achieved / goal;
            var result = 0m;
            if (r > 1.1m) result = 250m;
            if (r > 1.3m) result = 900m;
            result = Math.Round(result, 0);
            return result;
        }

        public string Short(string first, string last)
        {
            var a = first.Substring(0, 1);
            var b = last.Substring(0, 1);
            return a + b;
        }
    }
}
