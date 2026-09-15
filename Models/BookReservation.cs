namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Tracks a student's or professor's book reservation/borrow lifecycle.
    /// One record per borrow event. Status flows through:
    /// Pending → PickedUp → Returned (or Overdue → ReturnedLate)
    /// Also supports: Denied, Void (expired pickup window), Approved.
    /// </summary>
    public class BookReservation
    {
        public int Id { get; set; }

        /// <summary>FK to AspNetUsers — who made the reservation</summary>
        public string UserId { get; set; } = "";
        public ApplicationUser? User { get; set; }

        /// <summary>FK to Books — which book was reserved</summary>
        public int BookId { get; set; }
        public Book? Book { get; set; }

        /// <summary>When the student submitted the reservation online. Stored as UTC —
        /// convert with Helpers.PhTime.FromUtc() before displaying to a user.</summary>
        public DateTime ReservationDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Deadline by which the student must physically pick up the book (3-hour window).
        /// Auto-voided by BorrowController if not picked up in time.
        /// </summary>
        public DateTime PickupDeadline { get; set; }

        /// <summary>When the librarian physically handed the book to the student</summary>
        public DateTime? ActualPickupTime { get; set; }

        /// <summary>When the book must be returned (set to closing time 2 days from pickup)</summary>
        public DateTime? DueDate { get; set; }

        /// <summary>When the student actually returned the book</summary>
        public DateTime? ActualReturnDate { get; set; }

        /// <summary>
        /// Current state of the reservation:
        /// Pending = waiting for librarian approval / student pickup
        /// Approved = librarian approved (intermediate state)
        /// PickedUp = book is currently with the student
        /// Returned = returned on time
        /// Overdue = not returned by due date
        /// ReturnedLate = returned after due date (cleared to Returned after 3 days)
        /// Denied = librarian rejected the reservation
        /// Void = pickup window expired without the student collecting the book
        /// </summary>
        public string Status { get; set; } = "Pending";

        /// <summary>True once a return reminder email/SMS has been sent to avoid duplicate notifications</summary>
        public bool ReminderSent { get; set; } = false;

        /// <summary>Librarian's note (e.g. denial reason, auto-void reason)</summary>
        public string LibrarianRemarks { get; set; } = "";

        /// <summary>Stored as UTC — convert with Helpers.PhTime.FromUtc() before display.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
