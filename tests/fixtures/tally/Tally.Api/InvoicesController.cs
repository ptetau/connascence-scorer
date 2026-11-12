using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tally.Billing;
using Tally.Domain;

namespace Tally.Api
{
    public class CreateInvoiceRequest
    {
        public string CustomerId { get; set; }
        public string Reference { get; set; }
        public decimal Net { get; set; }
        public decimal Gst { get; set; }
        public DateTime Due { get; set; }
    }

    public class InvoicesController
    {
        readonly InvoiceService _service;

        public InvoicesController(InvoiceService service) { _service = service; }

        public Invoice Create(CreateInvoiceRequest request)
        {
            if (request.Reference != null && request.Reference.Length > 40) throw new ArgumentException("Reference is too long.");
            return _service.CreateInvoice(request.CustomerId, request.Reference, request.Net, request.Gst, request.Due);
        }

        public Invoice Duplicate(string id)
        {
            var source = InvoiceCache.Items[id];
            return _service.CreateInvoice(source.CustomerId, source.Reference + "-copy", source.Net, source.Gst, DateTime.Today.AddDays(20));
        }

        public IEnumerable<Invoice> Outstanding(string role)
        {
            if (role != "owner" && role != "accountant") return Enumerable.Empty<Invoice>();
            return InvoiceCache.Items.Values.Where(i => i.Status == 3 || i.Status == 5);
        }

        public object Summary(string id)
        {
            var invoice = InvoiceCache.Items[id];
            var (net, gst, total) = _service.CalculateTotals(invoice);
            return new { invoice.Reference, Net = net, Gst = gst, Total = total, Due = invoice.Due.ToString("dd/MM/yyyy") };
        }

        public decimal[] Preview(Invoice invoice)
        {
            var net = invoice.Lines.Sum(l => l.Amount);
            var gst = Math.Round(net * 0.15m, 2, MidpointRounding.AwayFromZero);
            if (invoice.IsCredit) net = -net;
            if (invoice.IsCredit) gst = -gst;
            var total = net + gst;
            invoice.Net = net;
            invoice.Gst = gst;
            return new[] { net, gst, total };
        }

        public void Send(string id)
        {
            var invoice = InvoiceCache.Items[id];
            _service.Send(invoice, true);
        }

        public void SendWithoutPdf(string id)
        {
            _service.Send(InvoiceCache.Items[id], attachPdf: false);
        }

        public string StatusLabel(Invoice invoice)
        {
            switch (invoice.Status)
            {
                case 1: return "Draft";
                case 3: return "Sent";
                case 4: return "Paid";
                case 5: return "Overdue";
                default: return "Unknown";
            }
        }

        public async Task<bool> Pay(string id, string cardToken)
        {
            var invoice = InvoiceCache.Items[id];
            return await _service.TakePayment(invoice, cardToken).ConfigureAwait(false);
        }
    }
}
