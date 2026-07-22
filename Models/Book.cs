namespace BookHiveLibrary.Models
{
    public class Book
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Category { get; set; } = "";
        public string GradeLevel { get; set; } = "";
        public string CallNumber { get; set; } = "";
        public string ISBN { get; set; } = "";
        public string AisleLocation { get; set; } = "";
        public string Description { get; set; } = "";
        public int TotalQuantity { get; set; } = 1;
        public int AvailableQuantity { get; set; } = 1;
        public bool IsArchived { get; set; } = false;
        public string ArchiveReason { get; set; } = "";
        public string CoverImageUrl { get; set; } = "";
        public string PublishedYear { get; set; } = "";
        public string BookFor { get; set; } = "Student"; // "Student" or "Professor"
        public bool IsRoomUseOnly { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public ICollection<BookReservation> Reservations { get; set; } = new List<BookReservation>();
    }
}
