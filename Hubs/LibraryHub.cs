using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BookHiveLibrary.Hubs
{
    [Authorize]
    public class LibraryHub : Hub
    {
        // Clients join a group per user ID so we can target individuals
        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (userId != null)
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

            await base.OnConnectedAsync();
        }
    }
}
