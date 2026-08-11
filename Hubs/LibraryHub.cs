using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BookHiveLibrary.Hubs
{
    public class LibraryHub : Hub
    {
        // Clients join a group per user ID so we can target individuals
        // Kiosk clients (unauthenticated) call JoinKiosk() to receive tap events
        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (userId != null)
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

            await base.OnConnectedAsync();
        }

        public async Task JoinLibrarian()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "librarians");
        }

        public async Task JoinKiosk()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "kiosk");
        }

        public async Task JoinTransaction()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "transaction");
        }

        public async Task JoinMIS()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "mis");
        }
    }
}
