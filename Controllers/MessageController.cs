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
    [Authorize]
    public class MessageController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub;

        public MessageController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<LibraryHub> hub)
        {
            _context = context;
            _userManager = userManager;
            _hub = hub;
        }

        // Inbox page
        public async Task<IActionResult> Index(string? withUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Challenge();

            // All users the current user has had a conversation with
            var contactIds = await _context.Messages
                .Where(m => m.SenderId == me.Id || m.ReceiverId == me.Id)
                .Select(m => m.SenderId == me.Id ? m.ReceiverId : m.SenderId)
                .Distinct()
                .ToListAsync();

            var contacts = await _userManager.Users
                .Where(u => contactIds.Contains(u.Id))
                .ToListAsync();

            // All users available to message (everyone except self)
            var allUsers = await _userManager.Users
                .Where(u => u.Id != me.Id && u.IsActive)
                .OrderBy(u => u.UserType).ThenBy(u => u.LastName)
                .ToListAsync();

            ViewBag.Me         = me;
            ViewBag.Contacts   = contacts;
            ViewBag.AllUsers   = allUsers;
            ViewBag.WithUserId = withUserId;
            ViewBag.UserType   = me.UserType;

            // Provide layout ViewBag values needed by role-specific layouts
            ViewBag.CurrentUserFullName = !string.IsNullOrWhiteSpace(me.FirstName)
                ? $"{me.FirstName} {me.LastName}".Trim()
                : me.Email;
            ViewBag.CurrentUserEmail = me.Email;
            ViewBag.CurrentUserPic   = me.ProfilePicture;

            return View();
        }

        // AJAX: get conversation messages between me and another user
        [HttpGet]
        public async Task<IActionResult> GetConversation(string otherUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var messages = await _context.Messages
                .Where(m => ((m.SenderId == me.Id && m.ReceiverId == otherUserId && !m.IsDeletedBySender) ||
                             (m.SenderId == otherUserId && m.ReceiverId == me.Id && !m.IsDeletedByReceiver)))
                .OrderBy(m => m.SentAt)
                .Select(m => new {
                    m.Id,
                    m.Content,
                    m.SenderId,
                    m.IsRead,
                    m.IsUnsent,
                    sentAt = m.SentAt.ToString("MMM d, h:mm tt")
                })
                .ToListAsync();

            // Mark received messages as read
            var unread = await _context.Messages
                .Where(m => m.SenderId == otherUserId && m.ReceiverId == me.Id && !m.IsRead)
                .ToListAsync();
            unread.ForEach(m => m.IsRead = true);
            if (unread.Any()) await _context.SaveChangesAsync();

            var other = await _userManager.FindByIdAsync(otherUserId);
            return Json(new {
                messages,
                myId = me.Id,
                otherName = other != null ? other.FirstName + " " + other.LastName : "Unknown",
                otherType = other?.UserType ?? ""
            });
        }

        // AJAX: send a message
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string receiverId, string content)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(content)) return BadRequest();

            var msg = new Message
            {
                SenderId   = me.Id,
                ReceiverId = receiverId,
                Content    = content.Trim(),
                SentAt     = DateTime.Now
            };
            _context.Messages.Add(msg);
            await _context.SaveChangesAsync();

            var senderName = $"{me.FirstName} {me.LastName}".Trim();
            var payload = new {
                msg.Id,
                msg.Content,
                msg.SenderId,
                senderName,
                sentAt = msg.SentAt.ToString("MMM d, h:mm tt")
            };

            // Push to receiver in real time
            await _hub.Clients.Group($"user-{receiverId}").SendAsync("ReceiveMessage", payload);

            return Json(payload);
        }

        // AJAX: unsend a message (removes for both sides)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unsend(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var msg = await _context.Messages.FindAsync(id);
            if (msg == null || msg.SenderId != me.Id) return Forbid();

            var receiverId = msg.ReceiverId;
            msg.IsUnsent = true;
            msg.Content  = "";
            await _context.SaveChangesAsync();

            await _hub.Clients.Group($"user-{receiverId}").SendAsync("MessageUnsent", new { msg.Id });
            return Json(new { success = true });
        }

        // AJAX: delete a message only for the current user
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteForMe(int id)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var msg = await _context.Messages.FindAsync(id);
            if (msg == null) return NotFound();
            if (msg.SenderId != me.Id && msg.ReceiverId != me.Id) return Forbid();

            if (msg.SenderId == me.Id)
                msg.IsDeletedBySender = true;
            else
                msg.IsDeletedByReceiver = true;

            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // AJAX: delete entire conversation for current user
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConversation(string otherUserId)
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var sent     = await _context.Messages.Where(m => m.SenderId == me.Id && m.ReceiverId == otherUserId).ToListAsync();
            var received = await _context.Messages.Where(m => m.SenderId == otherUserId && m.ReceiverId == me.Id).ToListAsync();

            sent.ForEach(m => m.IsDeletedBySender = true);
            received.ForEach(m => m.IsDeletedByReceiver = true);
            await _context.SaveChangesAsync();
            return Json(new { success = true });
        }

        // AJAX: unread count for badge
        [HttpGet]
        public async Task<IActionResult> UnreadCount()
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Json(new { count = 0 });

            var count = await _context.Messages
                .CountAsync(m => m.ReceiverId == me.Id && !m.IsRead);
            return Json(new { count });
        }

        // AJAX: conversation list for the sidebar
        [HttpGet]
        public async Task<IActionResult> GetConversationList()
        {
            var me = await _userManager.GetUserAsync(User);
            if (me == null) return Unauthorized();

            var allMsgs = await _context.Messages
                .Include(m => m.Sender)
                .Include(m => m.Receiver)
                .Where(m => (m.SenderId == me.Id && !m.IsDeletedBySender) ||
                            (m.ReceiverId == me.Id && !m.IsDeletedByReceiver))
                .OrderByDescending(m => m.SentAt)
                .ToListAsync();

            var convos = allMsgs
                .GroupBy(m => m.SenderId == me.Id ? m.ReceiverId : m.SenderId)
                .Select(g =>
                {
                    var last   = g.First();
                    var other  = last.SenderId == me.Id ? last.Receiver : last.Sender;
                    var unread = g.Count(m => m.ReceiverId == me.Id && !m.IsRead);
                    return new {
                        userId    = other?.Id ?? "",
                        name      = other != null ? other.FirstName + " " + other.LastName : "Unknown",
                        userType  = other?.UserType ?? "",
                        lastMsg       = last.IsUnsent ? "" : (last.Content.Length > 40 ? last.Content[..40] + "…" : last.Content),
                        lastMsgUnsent = last.IsUnsent,
                        sentAt        = last.SentAt.ToString("MMM d, h:mm tt"),
                        unread
                    };
                })
                .ToList();

            return Json(convos);
        }
    }
}
