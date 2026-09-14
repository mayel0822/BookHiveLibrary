using BookHiveLibrary.Data;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Controllers
{
    // This controller handles the internal messaging system inside BookHive.
    // Any logged-in user can send messages to other users (except MIS — they are excluded for privacy).
    //
    // Key features:
    //   - Inbox page showing all conversations
    //   - Real-time delivery using SignalR (messages appear instantly without page refresh)
    //   - Unsend: removes the message content for BOTH users
    //   - Delete for me: hides a message on your side only (the other person still sees it)
    //   - Delete conversation: hides all messages in a thread on your side only
    //   - Unread count badge shown in the navigation header
    [Authorize]
    public class MessageController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub; // Used to deliver messages in real time

        // PHT = UTC+8 (Philippine Time) — all DateTime values in the DB are UTC
        private static readonly TimeZoneInfo _pht =
            TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
                z.Id == "Asia/Manila" || z.Id == "Singapore Standard Time")
            ?? TimeZoneInfo.Utc;

        // Converts a UTC DateTime to PHT, then formats it as "Sep 5, 2:34 AM"
        private string ToPhtString(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), _pht)
            .ToString("MMM d, h:mm tt");

        public MessageController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<LibraryHub> hub)
        {
            _context     = context;
            _userManager = userManager;
            _hub         = hub;
        }

        // Shows the inbox page.
        // Loads:
        //   - contacts: everyone the current user has chatted with before
        //   - allUsers: everyone available to start a new conversation with (excluding MIS and yourself)
        //   - withUserId: if set, that conversation opens automatically (passed as a URL parameter)
        //
        // Also sets up the name, email, and profile picture for the page header (used by the layout).
        public async Task<IActionResult> Index(string? withUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Challenge();

            // Find all the users this person has had a conversation with
            var contactIds = await _context.Messages
                .Where(message => message.SenderId == me.Id || message.ReceiverId == me.Id)
                .Select(message => message.SenderId == me.Id ? message.ReceiverId : message.SenderId)
                .Distinct()
                .ToListAsync();

            var contacts = await _userManager.Users
                .Where(user => contactIds.Contains(user.Id))
                .ToListAsync();

            // All active users (except self and MIS) that can receive messages
            var allUsers = await _userManager.Users
                .Where(user => user.Id != me.Id && user.IsActive && user.UserType != "MIS")
                .OrderBy(user => user.UserType)
                .ThenBy(user => user.LastName)
                .ToListAsync();

            ViewBag.Me         = me;
            ViewBag.Contacts   = contacts;
            ViewBag.AllUsers   = allUsers;
            ViewBag.WithUserId = withUserId;
            ViewBag.UserType   = me.UserType;

            // These are needed by the role-specific layout files for the header bar
            string fullName = !string.IsNullOrWhiteSpace(me.FirstName)
                ? $"{me.FirstName} {me.LastName}".Trim()
                : me.Email ?? "";
            ViewBag.CurrentUserFullName = fullName;
            ViewBag.CurrentUserEmail    = me.Email;
            ViewBag.CurrentUserPic      = me.ProfilePicture;

            return View();
        }

        // Gets all the messages between the current user and one other user.
        // Called by JavaScript when the user clicks on a conversation in the sidebar.
        // Only returns messages the current user hasn't deleted on their side.
        // Also marks all unread received messages as "read" (clears the badge count).
        [HttpGet]
        public async Task<IActionResult> GetConversation(string otherUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var rawMessages = await _context.Messages
                .Where(message =>
                    // Messages I sent that I haven't deleted
                    (message.SenderId == me.Id && message.ReceiverId == otherUserId && !message.IsDeletedBySender) ||
                    // Messages I received that I haven't deleted
                    (message.SenderId == otherUserId && message.ReceiverId == me.Id && !message.IsDeletedByReceiver))
                .OrderBy(message => message.SentAt)
                .Select(message => new {
                    message.Id,
                    message.Content,
                    message.SenderId,
                    message.IsRead,
                    message.IsUnsent,
                    message.SentAt
                })
                .ToListAsync();

            // Convert UTC → PHT in memory (cannot be done inside EF Core SQL translation)
            var messages = rawMessages.Select(message => new {
                message.Id,
                message.Content,
                message.SenderId,
                message.IsRead,
                message.IsUnsent,
                sentAt = ToPhtString(message.SentAt)
            }).ToList();

            // Mark all messages received in this conversation as read
            var unreadMessages = await _context.Messages
                .Where(message => message.SenderId == otherUserId && message.ReceiverId == me.Id && !message.IsRead)
                .ToListAsync();

            foreach (var message in unreadMessages)
                message.IsRead = true;

            bool anyMarked = unreadMessages.Any();
            if (anyMarked) await _context.SaveChangesAsync();

            var otherUser = await _userManager.FindByIdAsync(otherUserId);
            string otherUserName = otherUser != null ? otherUser.FirstName + " " + otherUser.LastName : "Unknown";
            string otherUserType = otherUser?.UserType ?? "";

            return Json(new {
                messages,
                myId      = me.Id,
                otherName = otherUserName,
                otherType = otherUserType
            });
        }

        // Sends a message to another user.
        // Saves the message to the database, then immediately delivers it to the receiver's
        // screen in real time (they don't need to refresh — it just appears).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string receiverId, string content)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            bool messageIsEmpty = string.IsNullOrWhiteSpace(content);
            if (messageIsEmpty) return BadRequest();

            var newMessage = new Message
            {
                SenderId   = me.Id,
                ReceiverId = receiverId,
                Content    = content.Trim(),
                SentAt     = DateTime.UtcNow
            };
            _context.Messages.Add(newMessage);
            await _context.SaveChangesAsync();

            string senderName = $"{me.FirstName} {me.LastName}".Trim();
            var payload = new {
                newMessage.Id,
                newMessage.Content,
                newMessage.SenderId,
                senderName,
                sentAt = ToPhtString(newMessage.SentAt)
            };

            // Push the message to the receiver's screen immediately via SignalR
            await _hub.Clients.Group($"user-{receiverId}").SendAsync("ReceiveMessage", payload);

            return Json(payload);
        }

        // Unsends a message. Only the person who sent it can unsend it.
        // Clears the message text for BOTH the sender and receiver.
        // The receiver's screen is also updated in real time — the message bubble changes to "unsent".
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unsend(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var message = await _context.Messages.FindAsync(id);

            bool messageNotFound    = message == null;
            bool senderIsNotMe      = message?.SenderId != me.Id;
            if (messageNotFound || senderIsNotMe) return Forbid(); // Only the sender can unsend

            string receiverId  = message!.ReceiverId;
            message.IsUnsent   = true;
            message.Content    = ""; // Clear the text for both sides
            await _context.SaveChangesAsync();

            // Tell the receiver's screen to update the message bubble right away
            await _hub.Clients.Group($"user-{receiverId}").SendAsync("MessageUnsent", new { message.Id });
            return Json(new { success = true });
        }

        // Hides a message on the current user's side only.
        // The other person still sees the message — it's only removed from your view.
        // Works whether you sent it or received it.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteForMe(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var message = await _context.Messages.FindAsync(id);
            if (message == null) return NotFound();

            bool iAmNotPartOfThisConversation = message.SenderId != me.Id && message.ReceiverId != me.Id;
            if (iAmNotPartOfThisConversation) return Forbid();

            // Mark the message as deleted only for the person requesting it
            bool iSentThis = message.SenderId == me.Id;
            if (iSentThis)
                message.IsDeletedBySender = true;   // You sent it
            else
                message.IsDeletedByReceiver = true; // You received it

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // Hides an entire conversation on the current user's side.
        // All messages in the thread are hidden from your view — but the other person still sees them.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConversation(string otherUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            // Mark all messages I sent in this conversation as deleted by me
            var sentMessages = await _context.Messages
                .Where(message => message.SenderId == me.Id && message.ReceiverId == otherUserId)
                .ToListAsync();

            // Mark all messages I received in this conversation as deleted by me
            var receivedMessages = await _context.Messages
                .Where(message => message.SenderId == otherUserId && message.ReceiverId == me.Id)
                .ToListAsync();

            foreach (var message in sentMessages)
                message.IsDeletedBySender = true;

            foreach (var message in receivedMessages)
                message.IsDeletedByReceiver = true;

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // Returns how many unread messages the current user has.
        // This is called regularly by the layout header to keep the notification badge up to date.
        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Json(new { count = 0 });

            int unreadCount = await _context.Messages
                .CountAsync(message => message.ReceiverId == me.Id && !message.IsRead);

            return Json(new { count = unreadCount });
        }

        // Returns the list of conversations shown in the sidebar.
        // For each conversation it shows:
        //   - The other person's name and role (Student, Librarian, etc.)
        //   - A preview of the last message (cut off at 40 characters)
        //   - The time the last message was sent
        //   - How many unread messages are in that conversation
        //
        // Conversations where you've deleted all messages won't appear here.
        [HttpGet]
        public async Task<IActionResult> GetConversationList()
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            // Load all messages the user hasn't deleted on their side
            var allMessages = await _context.Messages
                .Include(message => message.Sender)
                .Include(message => message.Receiver)
                .Where(message =>
                    (message.SenderId == me.Id   && !message.IsDeletedBySender) ||
                    (message.ReceiverId == me.Id && !message.IsDeletedByReceiver))
                .OrderByDescending(message => message.SentAt)
                .ToListAsync();

            // Group by the other person in the conversation and build a summary for each
            var conversations = allMessages
                .GroupBy(message => message.SenderId == me.Id ? message.ReceiverId : message.SenderId)
                .Select(group =>
                {
                    var lastMessage  = group.First(); // The most recent message in this conversation
                    var otherPerson  = lastMessage.SenderId == me.Id ? lastMessage.Receiver : lastMessage.Sender;
                    int unreadCount  = group.Count(message => message.ReceiverId == me.Id && !message.IsRead);

                    // Truncate the message preview to 40 characters
                    string preview = lastMessage.IsUnsent
                        ? ""
                        : lastMessage.Content.Length > 40
                            ? lastMessage.Content[..40] + "…"
                            : lastMessage.Content;

                    return new {
                        userId        = otherPerson?.Id ?? "",
                        name          = otherPerson != null ? otherPerson.FirstName + " " + otherPerson.LastName : "Unknown",
                        userType      = otherPerson?.UserType ?? "",
                        lastMsg       = preview,
                        lastMsgUnsent = lastMessage.IsUnsent,
                        sentAt        = ToPhtString(lastMessage.SentAt),
                        unread        = unreadCount
                    };
                })
                .ToList();

            return Json(conversations);
        }
    }
}
