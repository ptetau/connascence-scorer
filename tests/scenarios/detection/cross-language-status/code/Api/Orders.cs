namespace Api
{
    public class Order { public int Status { get; set; } }
    public class Orders
    {
        public bool Shipped(Order o) => o.Status == 6;
        public void Ship(Order o) { o.Status = 6; }
    }
}
