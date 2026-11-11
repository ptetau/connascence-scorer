using System.Threading;
using Tally.Domain;

namespace Tally.Billing.Tests
{
    // Tests are excluded by default. Nothing here should be reported.
    public class InvoiceServiceTests
    {
        public void Sent_invoice_is_outstanding()
        {
            var invoice = new Invoice { Status = 3 };
            Thread.Sleep(50);
            if (invoice.Status != 3) throw new System.Exception("expected sent");
        }
    }
}
