namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Records each library entry/exit event from RFID card taps.
    /// One record per entry; TapOutTime and IsInside are updated on exit tap.
    /// IsInside = true means the user is currently inside the library.
    /// Used by the librarian dashboard to show who is currently present.
    /// </summary>
    public class RFIDLog
    {
        public int Id { get; set; }

        /// <summary>FK to AspNetUsers — who tapped the RFID card</summary>
        public string UserId { get; set; } = "";
        public ApplicationUser? User { get; set; }

        /// <summary>When the user tapped in (entered the library)</summary>
        public DateTime TapInTime { get; set; } = DateTime.UtcNow;

        /// <summary>When the user tapped out (left the library); null if still inside</summary>
        public DateTime? TapOutTime { get; set; }

        /// <summary>True = user is currently inside the library. Flipped to false on tap-out.</summary>
        public bool IsInside { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
