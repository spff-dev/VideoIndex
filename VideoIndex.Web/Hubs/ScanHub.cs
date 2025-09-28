using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace VideoIndex.Web.Hubs
{
    public class ScanHub : Hub
    {
        // Client calls this with a scanId (GUID as string) to join a group
        public Task JoinScan(string scanId)
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, $"scan:{scanId}");
        }
    }
}
