using System;
namespace App
{
    public class Orders
    {
        public void Place(string customer, string sku, string address, string coupon, string note)
        {
            if (customer == null) throw new ArgumentNullException(nameof(customer));
            if (sku == null) throw new ArgumentNullException(nameof(sku));
            if (address == null) throw new ArgumentNullException(nameof(address));
            if (coupon == null) throw new ArgumentNullException(nameof(coupon));
            if (note == null) throw new ArgumentNullException(nameof(note));
            Console.WriteLine("placed " + sku);
        }
    }
    public class Shipping
    {
        public void Book(string carrier, string from, string to, string service, string reference)
        {
            if (carrier == null) throw new ArgumentNullException(nameof(carrier));
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            Console.WriteLine("booked " + reference);
        }
    }
    public class Mapping
    {
        // Same shape, different members: not a copy.
        public object A(Orders o, Shipping s) { var x = o.GetHashCode(); var y = s.GetHashCode(); var z = x + y; Console.WriteLine(z); Console.WriteLine(x); return z; }
        public object B(Orders o, Shipping s) { var x = o.ToString(); var y = s.ToString(); var z = x + y; Console.WriteLine(z); Console.WriteLine(y); return z; }
    }
}
