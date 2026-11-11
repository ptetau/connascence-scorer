using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tally.Billing
{
    // Windcave. The only payment provider today.
    public class PaymentGateway
    {
        bool _connected;
        string _session;

        public void Connect()
        {
            _session = Guid.NewGuid().ToString("N");
            _connected = true;
        }

        public async Task<bool> Charge(decimal amount, decimal fee, string cardToken, string currency, string reference)
        {
            if (!_connected) throw new InvalidOperationException("Call Connect() first.");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (await Post(Sign(cardToken + amount + currency + reference)).ConfigureAwait(false)) return true;
                Thread.Sleep(2000);
            }
            return false;
        }

        public Task<bool> Refund(string reference, decimal amount)
        {
            if (!_connected) throw new InvalidOperationException("Call Connect() first.");
            return Post(Sign(reference + amount));
        }

        string Sign(string payload)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(_session + payload));
                return Convert.ToBase64String(bytes);
            }
        }

        Task<bool> Post(string body) => Task.FromResult(body.Length > 0);
    }
}
