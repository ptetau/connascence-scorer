using System;
namespace Sales
{
    public class Pricing
    {
        public decimal Commission(decimal sale)
        {
            var rate = 0.04m;
            if (sale > 5000m) rate = 0.06m;
            if (sale > 20000m) rate = 0.08m;
            var amount = sale * rate;
            amount = Math.Round(amount, 2);
            return amount;
        }

        public decimal Bonus(decimal target, decimal actual)
        {
            var ratio = actual / target;
            var bonus = 0m;
            if (ratio > 1.1m) bonus = 250m;
            if (ratio > 1.3m) bonus = 900m;
            bonus = Math.Round(bonus, 0);
            return bonus;
        }

        public string Initials(string first, string last)
        {
            var a = first.Substring(0, 1);
            var b = last.Substring(0, 1);
            return a + b;
        }
    }
}
