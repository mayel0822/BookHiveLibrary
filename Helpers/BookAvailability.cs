using BookHiveLibrary.Data;
using BookHiveLibrary.Models;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Helpers
{
    /// <summary>
    /// Recomputes Book.AvailableQuantity from scratch instead of incrementing/
    /// decrementing it by 1 at each pickup/return.
    ///
    /// Why: the stored field and the "real" availability (Total minus currently
    /// PickedUp/Overdue copies) could drift apart — e.g. BookController.Edit's own
    /// recompute only counted Status == "PickedUp" and missed "Overdue", so editing
    /// a book's title while a copy was overdue silently added a phantom available
    /// copy. Every +1/-1 site was a fresh chance for the same class of bug. This
    /// recomputes fresh from the authoritative source (current reservation
    /// statuses) every time, so a mistake in one call site can't leave the field
    /// permanently wrong — the next call anywhere fixes it.
    /// </summary>
    public static class BookAvailability
    {
        /// <summary>Recalculates and sets book.AvailableQuantity. Does not save —
        /// call SaveChangesAsync() as part of whatever else the caller is doing.</summary>
        public static async Task Recalculate(ApplicationDbContext context, Book book)
        {
            int borrowedCopies = await context.BookReservations.CountAsync(reservation =>
                reservation.BookId == book.Id &&
                (reservation.Status == "PickedUp" || reservation.Status == "Overdue"));

            book.AvailableQuantity = Math.Max(0, book.TotalQuantity - borrowedCopies);
        }
    }
}
