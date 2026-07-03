using System.Collections.Generic;
using System.Threading.Tasks;
using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    public interface IRemnawaveService
    {
        Task<SubscriptionResult> FetchServersAsync(string subscriptionUrl);
    }
}
