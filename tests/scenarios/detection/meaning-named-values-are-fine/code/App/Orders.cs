namespace App
{
    public enum OrderStatus { New = 1, Shipped = 3, Returned = 7 }
    public static class Limits { public const int Shipped = 3; }
    public class Order { public OrderStatus Status { get; set; } public int Code { get; set; } }
    public class Shipping
    {
        public bool CanShip(Order o) => o.Status != OrderStatus.Shipped;
        public void Ship(Order o) { o.Status = OrderStatus.Shipped; }
        public bool Legacy(Order o) => o.Code == Limits.Shipped;
    }
    public class Returns
    {
        public bool CanReturn(Order o) => o.Status == OrderStatus.Shipped;
        public void Return(Order o) { o.Status = OrderStatus.Returned; o.Code = Limits.Shipped; }
    }
}
