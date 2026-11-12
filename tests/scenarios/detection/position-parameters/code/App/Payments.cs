using System;
namespace App
{
    public class Payments
    {
        public void Transfer(string fromAccount, string toAccount, decimal amount, string reference) { }
        public void Book(Guid id, DateTime when, int seats, string note) { }
        public void Refund(string fromAccount, string toAccount, decimal amount, string reference) { }
        public (int Min, int Max) Range() => (1, 9);
        public (string First, string Last, string Email) Name() => ("a", "b", "c");
    }
    public class Caller
    {
        readonly Payments _p = new Payments();
        public void Run()
        {
            _p.Transfer("12-01", "12-02", 10m, "rent");
            _p.Book(Guid.NewGuid(), DateTime.Now, 2, "window");
            _p.Refund(fromAccount: "12-02", toAccount: "12-01", amount: 10m, reference: "rent");
            var (min, max) = _p.Range();
            var (first, last, email) = _p.Name();
        }
    }
}
