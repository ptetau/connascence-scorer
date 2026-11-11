using System;
using System.Security.Cryptography;
using System.Text;
using Tally.Billing;

namespace Tally.Api
{
    // Windcave calls this when a card payment settles.
    public class WebhookController
    {
        readonly string _secret;

        public WebhookController(string secret) { _secret = secret; }

        public bool Receive(string invoiceId, string body, string signature)
        {
            if (!Verify(body, signature)) return false;
            var invoice = InvoiceCache.Items[invoiceId];
            invoice.AmountPaid = invoice.Total;
            invoice.Status = 4;
            return true;
        }

        bool Verify(string body, string signature)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(_secret + body));
                return Convert.ToBase64String(bytes) == signature;
            }
        }
    }
}
