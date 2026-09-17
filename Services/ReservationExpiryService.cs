using BookHiveLibrary.Data;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Services
{
    // Shared by BorrowController.Index() (runs whenever a librarian opens the
    // transaction page) and ReminderBackgroundService (runs hourly regardless of
    // whether anyone opens that page), so a reservation is voided, the student
    // notified, and the change pushed live exactly once no matter which caller
    // catches it first — whichever runs first flips Status away from "Pending",
    // so the other's query simply won't match that reservation again.
    public static class ReservationExpiryService
    {
        public static async Task<int> VoidExpiredPendingReservationsAsync(ApplicationDbContext context, IHubContext<LibraryHub> hub)
        {
            var expired = await context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "Pending" && reservation.PickupDeadline < DateTime.UtcNow)
                .ToListAsync();

            if (!expired.Any()) return 0;

            foreach (var reservation in expired)
            {
                reservation.Status           = "Void";
                reservation.LibrarianRemarks = "Auto-voided: not picked up within the reservation window.";

                context.StudentNotifications.Add(new StudentNotification
                {
                    UserId  = reservation.UserId,
                    Type    = "Void",
                    Title   = reservation.Book?.Title ?? "Book",
                    Message = $"Your reservation for \"{reservation.Book?.Title}\" expired because it wasn't picked up in time.",
                });
            }

            await context.SaveChangesAsync();

            // Live-push to each affected student — Dashboard.cshtml already reloads
            // itself on this event, so the voided reservation disappears from "My
            // Reservation" without the student needing to manually refresh.
            foreach (var reservation in expired)
                await hub.Clients.Group($"user-{reservation.UserId}").SendAsync("BookTransactionUpdated", new { action = "Void" });

            return expired.Count;
        }
    }
}
