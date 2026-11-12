using System;
using System.Collections.Generic;

namespace Tally.Domain
{
    public enum InvoiceKind
    {
        Standard,
        Credit,
    }

    public class Invoice
    {
        public string Id { get; set; }
        public string CustomerId { get; set; }
        public string Reference { get; set; }
        public InvoiceKind Kind { get; set; }

        // 1 = draft, 3 = sent, 4 = paid, 5 = overdue
        public int Status { get; set; }

        public decimal Net { get; set; }
        public decimal Gst { get; set; }
        public decimal Total { get; set; }
        public decimal AmountPaid { get; set; }
        public DateTime Due { get; set; }
        public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();

        public bool IsCredit => Kind == InvoiceKind.Credit;

        public bool CanEdit() => Status == 1;

        public bool IsOutstanding() => Status == 3 || Status == 5;

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Reference)) errors.Add("Reference is required.");
            else if (Reference.Length > 40) errors.Add("Reference is too long.");
            if (Lines.Count == 0) errors.Add("Add at least one line.");
            return errors;
        }
    }

    public class InvoiceLine
    {
        public string Description { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount => Quantity * UnitPrice;
    }
}
