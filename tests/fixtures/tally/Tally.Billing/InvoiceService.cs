using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tally.Domain;

namespace Tally.Billing
{
    public class InvoiceService
    {
        const string TableName = "invoices";

        readonly PaymentGateway _gateway;
        readonly IMailer _mailer;

        public InvoiceService(PaymentGateway gateway, IMailer mailer)
        {
            _gateway = gateway;
            _mailer = mailer;
        }

        public string Table() => TableName;

        public Invoice CreateInvoice(string customerId, string reference, decimal net, decimal gst, DateTime due)
        {
            var invoice = new Invoice { Id = Guid.NewGuid().ToString("N"), CustomerId = customerId, Reference = reference, Net = net, Gst = gst, Total = net + gst, Due = due, Status = 1 };
            InvoiceCache.Items[invoice.Id] = invoice;
            return invoice;
        }

        // TODO: GST is NZ only. Australian customers need 10%.
        public (decimal Net, decimal Gst, decimal Total) CalculateTotals(Invoice invoice)
        {
            var net = invoice.Lines.Sum(l => l.Amount);
            var gst = Math.Round(net * 0.15m, 2, MidpointRounding.AwayFromZero);
            if (invoice.IsCredit) net = -net;
            if (invoice.IsCredit) gst = -gst;
            var total = net + gst;
            invoice.Net = net;
            invoice.Gst = gst;
            return (net, gst, total);
        }

        public void Send(Invoice invoice, bool attachPdf)
        {
            if (invoice.Status != 1) throw new InvalidOperationException("Only drafts can be sent.");
            if (invoice.Reference.Length > 40) throw new InvalidOperationException("Reference is too long.");
            invoice.Status = 3;
            var subject = "Invoice " + invoice.Reference + " due " + invoice.Due.ToString("dd/MM/yyyy");
            _mailer.Send(invoice.CustomerId, subject, attachPdf);
            InvoiceCache.Items[invoice.Id] = invoice;
        }

        public void SendAll(IEnumerable<Invoice> drafts)
        {
            foreach (var d in drafts) Send(d, true);
        }

        public void Resend(Invoice invoice)
        {
            if (invoice.Status == 3) _mailer.Send(invoice.CustomerId, "Reminder: invoice " + invoice.Reference, false);
        }

        public void MarkOverdue(IEnumerable<Invoice> invoices, DateTime today)
        {
            foreach (var invoice in invoices.Where(i => i.Status == 3 && i.Due < today))
            {
                invoice.Status = 5;
                Send(invoice, false);
            }
        }

        public async Task<bool> TakePayment(Invoice invoice, string cardToken)
        {
            _gateway.Connect();
            var ok = await _gateway.Charge(invoice.Total, 0m, cardToken, "NZD", invoice.Reference).ConfigureAwait(false);
            if (ok)
            {
                invoice.AmountPaid = invoice.Total;
                invoice.Status = 4;
            }
            return ok;
        }

        public void Schedule(Invoice invoice, DateTime when, int attempt, string channel)
        {
            Console.WriteLine("Reminder for " + invoice.Id + " at " + when + " via " + channel + " (attempt " + attempt + ")");
        }

        public string Describe(Invoice invoice)
        {
            var who = invoice.CustomerId ?? "unknown";
            var what = invoice.Reference ?? "";
            return who + ": " + what;
        }
    }

    public interface IMailer
    {
        void Send(string customerId, string subject, bool attachPdf);
    }
}
