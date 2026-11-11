using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Shop.Orders
{
    public class OrderService
    {
        public void Ship(Order order, bool notifyCustomer)
        {
            if (order.Status == 3) return;
            order.Status = 3;
            OrderCache.Items[order.Id] = order;
            if (notifyCustomer) Console.WriteLine("Shipped " + order.Id);
        }

        public Invoice CreateInvoice(decimal net, decimal tax, decimal discount, decimal total, string currency)
        {
            return new Invoice { Net = net, Tax = tax, Total = total - discount, Currency = currency };
        }

        public void Process(Order order)
        {
            var invoice = CreateInvoice(order.Net, order.Tax, 0m, order.Net + order.Tax, "NZD");
            Ship(order, true);
            Thread.Sleep(500);
            Console.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-dd") + " " + invoice.Total);
        }

        public string Signature(Order order)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(order.Id + "|" + order.Net));
                return Convert.ToBase64String(bytes);
            }
        }

        public decimal Discount(Order order)
        {
            var total = order.Net + order.Tax;
            var rate = 0m;
            if (total > 500m) rate = 0.05m;
            if (total > 1000m) rate = 0.1m;
            var discount = total * rate;
            Console.WriteLine("Discount " + discount);
            return discount;
        }

        public Order ParseLine(string line)
        {
            var parts = line.Split(',');
            return new Order { Id = int.Parse(parts[0]), CustomerName = parts[1], Status = int.Parse(parts[2]) };
        }
    }

    public class Invoice
    {
        public decimal Net { get; set; }
        public decimal Tax { get; set; }
        public decimal Total { get; set; }
        public string Currency { get; set; }
    }

    public class PaymentClient
    {
        private bool _connected;

        public void Connect(string endpoint) { _connected = true; }

        public void Charge(Order order)
        {
            if (!_connected) throw new InvalidOperationException("Call Connect first");
            Console.WriteLine("Charging " + order.Id);
        }
    }
}
