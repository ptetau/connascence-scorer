using System.Collections.Generic;

namespace Shop.Orders
{
    public class Order
    {
        public const int MaxNameLength = 50;

        public int Id { get; set; }
        public string CustomerName { get; set; }
        public int Status { get; set; }          // 0 new, 1 paid, 3 shipped
        public decimal Net { get; set; }
        public decimal Tax { get; set; }
    }

    public static class OrderCache
    {
        public static Dictionary<int, Order> Items = new Dictionary<int, Order>();
    }
}
