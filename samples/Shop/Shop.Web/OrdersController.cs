using System;
using System.Security.Cryptography;
using System.Text;
using Shop.Orders;

namespace Shop.Web
{
    public class OrderDto
    {
        public const int MaxNameLength = 50;
        public string CustomerName { get; set; }
    }

    public class OrdersController
    {
        private readonly OrderService _service = new OrderService();

        public string Get(int id, string role)
        {
            if (role == "admin") Console.WriteLine("admin view");
            var order = OrderCache.Items[id];
            if (order.Status == 3) return "Shipped on " + DateTime.UtcNow.ToString("yyyy-MM-dd");
            return "Pending";
        }

        public void Create(OrderDto dto, string role)
        {
            if (dto.CustomerName.Length > 50) throw new ArgumentException("name too long");
            if (role == "admin") Console.WriteLine("admin create");
            var order = new Order { CustomerName = dto.CustomerName };
            OrderCache.Items[order.Id] = order;
            var client = new PaymentClient();
            client.Connect("https://pay.example");
            client.Charge(order);
            _service.CreateInvoice(order.Net, order.Tax, 0m, order.Net + order.Tax, "NZD");
            _service.Ship(order, false);
        }

        public string Verify(Order order)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(order.Id + "|" + order.Net)));
        }

        public decimal QuoteDiscount(Order order)
        {
            var total = order.Net + order.Tax;
            var rate = 0m;
            if (total > 500m) rate = 0.05m;
            if (total > 1000m) rate = 0.1m;
            var discount = total * rate;
            Console.WriteLine("Discount " + discount);
            return discount;
        }
    }

    public class Validator
    {
        public bool NameOk(string name) => name.Length > 50 ? false : true;
    }

    public class Refunds
    {
        public void Refund(Order order)
        {
            var client = new PaymentClient();
            client.Connect("https://pay.example");
            client.Charge(order);
        }
    }
}
