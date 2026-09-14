namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Tracks one student's use of a library computer.
    /// Created when librarian starts a session; EndTime is set when session ends.
    /// IsActive = true while the session is running; flipped to false on end.
    /// The student kiosk page polls KioskStatus to show remaining time.
    /// </summary>
    public class ComputerSession
    {
        public int Id { get; set; }

        /// <summary>FK to AspNetUsers — the student using the computer</summary>
        public string UserId { get; set; } = "";
        public ApplicationUser? User { get; set; }

        /// <summary>FK to ComputerUnits — which computer the student is using</summary>
        public int ComputerUnitId { get; set; }
        public ComputerUnit? ComputerUnit { get; set; }

        public DateTime StartTime { get; set; } = DateTime.Now;

        /// <summary>Null while session is active; set when librarian ends or timer runs out</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>True while the session is running; false after it ends</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Base time limit for the session (default 60 minutes)</summary>
        public int AllowedMinutes { get; set; } = 60;

        /// <summary>Extra minutes added by the librarian via the Extend button (stacks)</summary>
        public int ExtendedMinutes { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
