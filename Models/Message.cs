namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Represents a direct message between two BookHive users.
    /// Messages support soft-deletion per side (deleted for sender only, or receiver only),
    /// unsend (clears content for both sides), and read receipts.
    /// MIS users cannot send or receive messages (excluded from allUsers in MessageController).
    /// Real-time delivery is handled by SignalR (LibraryHub, "user-{id}" group).
    /// </summary>
    public class Message
    {
        public int Id { get; set; }

        /// <summary>FK to AspNetUsers — who sent the message</summary>
        public string SenderId { get; set; } = "";
        public ApplicationUser? Sender { get; set; }

        /// <summary>FK to AspNetUsers — who receives the message</summary>
        public string ReceiverId { get; set; } = "";
        public ApplicationUser? Receiver { get; set; }

        /// <summary>Message body; set to "" when IsUnsent = true</summary>
        public string Content { get; set; } = "";

        public DateTime SentAt { get; set; } = DateTime.Now;

        /// <summary>True once the receiver opens the conversation</summary>
        public bool IsRead { get; set; } = false;

        /// <summary>True if sender unsent the message — shown as "Message unsent" on both sides</summary>
        public bool IsUnsent { get; set; } = false;

        /// <summary>True if the sender deleted the message for themselves only</summary>
        public bool IsDeletedBySender { get; set; } = false;

        /// <summary>True if the receiver deleted the message for themselves only</summary>
        public bool IsDeletedByReceiver { get; set; } = false;
    }
}
