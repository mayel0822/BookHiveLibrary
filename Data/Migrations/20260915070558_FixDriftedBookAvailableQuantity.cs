using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookHiveLibrary.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixDriftedBookAvailableQuantity : Migration
    {
        /// <inheritdoc />
        // One-time data repair, not a schema change. Books.AvailableQuantity was
        // maintained by +1/-1 arithmetic scattered across five different actions
        // (see Helpers/BookAvailability, added alongside this migration) — any one
        // of them missing a case (confirmed: BookController.Edit's own recompute
        // only counted Status == "PickedUp" and silently ignored "Overdue") could
        // leave the stored value permanently wrong, with nothing to ever correct it
        // again unless that exact book happened to go through another borrow/return.
        // This recalculates every book's AvailableQuantity from the authoritative
        // source — its current reservations — the same formula the stat cards on
        // Book/Index and Book/Register already use ("Available = Total − Borrowed").
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE b
                SET b.AvailableQuantity = CASE
                        WHEN b.TotalQuantity - ISNULL(borrowed.Cnt, 0) < 0 THEN 0
                        ELSE b.TotalQuantity - ISNULL(borrowed.Cnt, 0)
                    END
                FROM Books b
                LEFT JOIN (
                    SELECT BookId, COUNT(*) AS Cnt
                    FROM BookReservations
                    WHERE Status IN ('PickedUp', 'Overdue')
                    GROUP BY BookId
                ) borrowed ON borrowed.BookId = b.Id;
            ");
        }

        /// <inheritdoc />
        // Deliberately a no-op: there's no prior "wrong" value worth restoring —
        // the whole point of Up() is that the previous value was already incorrect.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
