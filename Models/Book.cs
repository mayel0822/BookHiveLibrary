namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Represents a book in the library catalog.
    /// Stored in the Books table. Tracks quantity so multiple copies can exist.
    /// Books can be soft-archived (IsArchived = true) instead of permanently deleted.
    /// </summary>
    public class Book
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";

        /// <summary>Used for filtering (e.g. Fiction, Science, Reference)</summary>
        public string Category { get; set; } = "";

        /// <summary>Target academic level (e.g. "JHS", "SHS", "Tertiary")</summary>
        public string GradeLevel { get; set; } = "";

        /// <summary>Library call number used for shelf location (e.g. "REF 620 B12")</summary>
        public string CallNumber { get; set; } = "";

        public string ISBN { get; set; } = "";

        /// <summary>Physical aisle label in the library where the book is shelved</summary>
        public string AisleLocation { get; set; } = "";

        public string Description { get; set; } = "";

        /// <summary>Total physical copies owned by the library</summary>
        public int TotalQuantity { get; set; } = 1;

        /// <summary>Copies currently available to borrow (decremented on pickup, incremented on return)</summary>
        public int AvailableQuantity { get; set; } = 1;

        /// <summary>Soft-delete flag — archived books are hidden from catalog but records are kept</summary>
        public bool IsArchived { get; set; } = false;

        public string ArchiveReason { get; set; } = "";

        /// <summary>URL or path to cover image (used in catalog display)</summary>
        public string CoverImageUrl { get; set; } = "";

        public string PublishedYear { get; set; } = "";

        /// <summary>
        /// Who can reserve this book: "Student" (default) or "Professor".
        /// Students cannot reserve professor-only books.
        /// </summary>
        public string BookFor { get; set; } = "Student";

        /// <summary>
        /// If true, the book cannot be borrowed outside the library.
        /// Students can only read it inside (e.g. reference books, magazines).
        /// </summary>
        public bool IsRoomUseOnly { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Navigation property — all reservation records for this book
        public ICollection<BookReservation> Reservations { get; set; } = new List<BookReservation>();
    }
}
