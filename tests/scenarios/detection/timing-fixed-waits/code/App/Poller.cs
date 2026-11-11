using System;
using System.Threading;
using System.Threading.Tasks;
namespace App
{
    public class Poller
    {
        public async Task<bool> WaitForExport(Func<bool> done)
        {
            await Task.Delay(1500);
            return done();
        }
        public void Settle() { Thread.Sleep(TimeSpan.FromSeconds(3)); }
        public Task Wait(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
    }
}
