using System.Collections.Generic;
using Tally.Domain;

namespace Tally.Billing
{
    // Keeps recent invoices in memory so the list page is fast.
    public static class InvoiceCache
    {
        public static Dictionary<string, Invoice> Items = new Dictionary<string, Invoice>();
    }
}
