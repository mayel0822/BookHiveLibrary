using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace BookHiveLibrary.Hubs
{
    // SignalR hub for BookHive real-time features.
    //
    // "Hub" = a central place that connected browser tabs can call methods on,
    // and that the server can push events to, without any page reload.
    //
    // Every connected browser tab joins a personal group named "user-{userId}".
    // Staff pages (librarians, kiosk, transaction desk, MIS) call extra Join methods
    // to subscribe to their own broadcast groups.
    //
    // Events pushed by the server (sent via IHubContext<LibraryHub> in controllers):
    //   KioskTap                — RFID tap detected at the library entrance
    //   LibraryEntryUpdated     — Student's inside/outside status changed
    //   NewReservation          — A student just made a new book reservation
    //   BookTransactionUpdated  — A borrow/return was processed
    //   ComputerSessionUpdated  — A computer session started or ended
    //   DeskRfidTap             — RFID tap at the librarian's desk
    //   UnregisteredRfidTap     — An unknown RFID card was scanned
    //   ReceiveMessage          — A new chat message was sent to this user
    //   MessageUnsent           — A previously sent message was unsent
    //   BookAvailabilityChanged — A book's available copy count changed (pickup/return)
    public class LibraryHub : Hub
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public LibraryHub(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        // Called automatically when a browser tab connects to the hub.
        // Adds this connection to the user's personal group so the server
        // can send messages directly to them later.
        public override async Task OnConnectedAsync()
        {
            // Only authenticated users get a personal group
            bool userIsLoggedIn = Context.User?.Identity?.IsAuthenticated == true;
            if (userIsLoggedIn)
            {
                var user = await _userManager.GetUserAsync(Context.User!);
                if (user != null)
                {
                    string personalGroup = $"user-{user.Id}";
                    await Groups.AddToGroupAsync(Context.ConnectionId, personalGroup);

                    // Students and professors auto-join "students" so the server can
                    // broadcast catalog-wide changes (e.g. a book running out of copies)
                    // to everyone browsing, not just whoever's own reservation changed.
                    // Done here (not via an explicit Join call from the page) so it can't
                    // be lost on reconnect the way group joins tied to a client invoke can.
                    bool isStudentOrProfessor = user.UserType == "Student" || user.UserType == "Professor";
                    if (isStudentOrProfessor)
                        await Groups.AddToGroupAsync(Context.ConnectionId, "students");
                }
            }

            await base.OnConnectedAsync();
        }

        // Joins the "librarians" broadcast group.
        // Called by the librarian's dashboard page on load.
        // The server sends reservation and transaction events to this group.
        public async Task JoinLibrarian()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "librarians");
        }

        // Joins the "kiosk" group.
        // Called by the RFID kiosk page at the library entrance.
        // The server sends entry/exit tap events to this group.
        public async Task JoinKiosk()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "kiosk");
        }

        // Joins the "transaction" group.
        // Called by the librarian's computer transaction page.
        // The server sends computer session start/end events to this group.
        public async Task JoinTransaction()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "transaction");
        }

        // Joins the "mis" group.
        // Called by MIS dashboard pages.
        // Used for MIS-specific real-time updates.
        public async Task JoinMIS()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "mis");
        }
    }
}
