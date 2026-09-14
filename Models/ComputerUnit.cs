namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Represents a physical computer in the library.
    /// Librarians assign students to available computers; sessions are tracked in ComputerSession.
    /// Archived computers are hidden from the transaction page but records are kept.
    /// </summary>
    public class ComputerUnit
    {
        public int Id { get; set; }

        /// <summary>Label shown on the kiosk and librarian UI (e.g. "PC-01", "PC-02")</summary>
        public string ComputerNumber { get; set; } = "";

        /// <summary>True = no active session; False = currently in use by a student</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>Soft-delete — archived computers are hidden but history is preserved</summary>
        public bool IsArchived { get; set; } = false;

        public string ArchiveReason { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // All sessions (past and present) for this computer
        public ICollection<ComputerSession> Sessions { get; set; } = new List<ComputerSession>();
    }
}
