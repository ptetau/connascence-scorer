using System.Threading.Tasks;
namespace App.Services
{
    // A fake shipped with the app for offline demos (also seen in eShop). Treated like test code.
    public class BasketMockService
    {
        public async Task<int> Count() { await Task.Delay(10); return 3; }
        public async Task Clear() { await Task.Delay(10); }
    }
}
