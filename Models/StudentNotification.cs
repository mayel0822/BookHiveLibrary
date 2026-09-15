namespace BookHiveLibrary.Models
{
    /// <summary>
    /// A persisted, one-time notification for a student/professor — something that
    /// HAPPENED (a reservation was denied, a book they're waiting on ran out), as
    /// opposed to the notification bell's other items (pending pickup, due soon),
    /// which are computed live from current reservation state and correctly
    /// disappear once resolved.
    ///
    /// These records don't disappear on their own — the student marks them read,
    /// which clears the bell's unread badge but leaves the notification visible in
    /// the list. A new one appearing doesn't replace an older one; it's just
    /// another row, shown newest first.
    /// </summary>
    public class StudentNotification
    {
        public int Id { get; set; }

        /// <summary>FK to AspNetUsers — who this notification is for</summary>
        public string UserId { get; set; } = "";
        public ApplicationUser? User { get; set; }

        /// <summary>"Denied" or "Unavailable" — drives the badge color in the bell UI</summary>
        public string Type { get; set; } = "";

        public string Title { get; set; } = "";
        public string Message { get; set; } = "";

        /// <summary>Stored as UTC — convert with Helpers.PhTime.FromUtc() before display.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>True once the student has opened the notification bell since this
        /// was created. Only affects the unread badge count — read notifications stay
        /// in the list.</summary>
        public bool IsRead { get; set; } = false;
    }
}
