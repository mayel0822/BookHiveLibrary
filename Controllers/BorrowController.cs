using BookHiveLibrary.Data;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Controllers
{
    public class BorrowController : Controller
    {
        private const int MaxBooksPerUser = 3;
        private const int BorrowDays = 2;
        private const int ReservationWindowHours = 3;
        private static readonly TimeSpan LibraryOpenTime  = new TimeSpan(8, 0, 0);
        private static readonly TimeSpan LibraryCloseTime = new TimeSpan(16, 30, 0);

        private static DateTime CalculateDueDate() =>
            DateTime.Today.AddDays(BorrowDays).Date + LibraryCloseTime;

        private static DateTime CalculatePickupDeadline()
        {
            var now = DateTime.Now;
            var openToday = DateTime.Today + LibraryOpenTime;
            var start = now < openToday ? openToday : now;
            return start.AddHours(ReservationWindowHours);
        }

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly BookHiveLibrary.Services.EmailService _emailService;
        private readonly BookHiveLibrary.Services.SmsService _smsService;
        private readonly IHubContext<LibraryHub> _hub;

        public BorrowController(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
            BookHiveLibrary.Services.EmailService emailService,
            BookHiveLibrary.Services.SmsService smsService,
            IHubContext<LibraryHub> hub)
        {
            _context = context;
            _userManager = userManager;
            _emailService = emailService;
            _smsService = smsService;
            _hub = hub;
        }

        // Helper: push a book transaction event to all librarians + optionally a specific student
        private async Task PushBookEvent(string eventName, object payload, string? studentUserId = null)
        {
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var lib in librarians)
                await _hub.Clients.Group($"user-{lib.Id}").SendAsync(eventName, payload);
            if (studentUserId != null)
                await _hub.Clients.Group($"user-{studentUserId}").SendAsync(eventName, payload);
        }

        private async Task LoadBorrowFormData()
        {
            ViewBag.Students = await _userManager.Users
                .Where(u => (u.UserType == "Student" || u.UserType == "Professor") && u.IsActive)
                .OrderBy(u => u.LastName)
                .ToListAsync();
            ViewBag.AvailableBooks = await _context.Books
                .Where(b => !b.IsArchived && b.AvailableQuantity > 0)
                .OrderBy(b => b.Title)
                .ToListAsync();
        }

        // Transaction Management - Book
        public async Task<IActionResult> Index()
        {
            // Auto-void reservations past the 3-hour pickup window
            var expiredReservations = await _context.BookReservations
                .Where(r => r.Status == "Pending" && r.PickupDeadline < DateTime.Now)
                .ToListAsync();
            foreach (var exp in expiredReservations)
            {
                exp.Status = "Void";
                exp.LibrarianRemarks = "Auto-voided: not picked up within 3 hours.";
            }
            if (expiredReservations.Any())
                await _context.SaveChangesAsync();

            // Auto-mark books as Overdue when due date has passed
            var overdueBooks = await _context.BookReservations
                .Where(r => r.Status == "PickedUp" && r.DueDate < DateTime.Now)
                .ToListAsync();
            foreach (var overdue in overdueBooks)
                overdue.Status = "Overdue";
            if (overdueBooks.Any())
                await _context.SaveChangesAsync();

            // Auto-clear ReturnedLate records 3 days after return
            var lateClearCutoff = DateTime.Now.AddDays(-3);
            var clearedLate = await _context.BookReservations
                .Where(r => r.Status == "ReturnedLate" && r.ActualReturnDate < lateClearCutoff)
                .ToListAsync();
            foreach (var r in clearedLate)
                r.Status = "Returned";
            if (clearedLate.Any())
                await _context.SaveChangesAsync();

            // Pending reservations panel
            ViewBag.PendingReservations = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "Pending")
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(s => s.SectionName, s => s.AdviserName);

            // Send Reminder panel — due within 12 hours AND reminder NOT yet sent
            var dueCutoff = DateTime.Now.AddHours(12);
            ViewBag.BooksDue = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" && r.DueDate <= dueCutoff && !r.ReminderSent)
                .OrderBy(r => r.DueDate)
                .ToListAsync();

            ViewBag.BooksDueCount = ((List<BookReservation>)ViewBag.BooksDue).Count;

            // All Books Due panel — all active borrowed books with due dates
            ViewBag.AllBooksDue = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" || r.Status == "Overdue")
                .OrderBy(r => r.DueDate)
                .ToListAsync();

            // Borrowed books table
            var borrowedBooks = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" || r.Status == "Overdue" || r.Status == "Approved" || r.Status == "ReturnedLate")
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            ViewBag.PendingCount = await _context.BookReservations.CountAsync(r => r.Status == "Pending");

            await LoadBorrowFormData();
            return View(borrowedBooks);
        }

        // Librarian creates a borrow record directly (walk-up)
        [HttpPost]
        public async Task<IActionResult> CreateBorrow(string userId, int bookId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            var book = await _context.Books.FindAsync(bookId);

            if (user == null || book == null)
            {
                TempData["Error"] = "Invalid user or book.";
                return RedirectToAction("Index");
            }

            int activeCount = await _context.BookReservations.CountAsync(r =>
                r.UserId == userId &&
                (r.Status == "PickedUp" || r.Status == "Overdue"));

            if (activeCount >= MaxBooksPerUser)
            {
                TempData["Error"] = $"This user already has {MaxBooksPerUser} books borrowed.";
                return RedirectToAction("Index");
            }

            if (book.AvailableQuantity <= 0)
            {
                TempData["Error"] = "No copies available for this book.";
                return RedirectToAction("Index");
            }

            if (book.IsRoomUseOnly)
            {
                TempData["Error"] = "This book is for room use only and cannot be borrowed outside the library.";
                return RedirectToAction("Index");
            }

            book.AvailableQuantity -= 1;

            _context.BookReservations.Add(new BookReservation
            {
                UserId = userId,
                BookId = bookId,
                Status = "PickedUp",
                ActualPickupTime = DateTime.Now,
                DueDate = CalculateDueDate(),
                PickupDeadline = DateTime.Now
            });

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "PickedUp", book = book.Title, user = $"{user.FirstName} {user.LastName}" }, userId);
            TempData["Success"] = $"Book \"{book.Title}\" borrowed by {user.FirstName} {user.LastName}. Due in {BorrowDays} days.";
            return RedirectToAction("Index");
        }

        // Send reminder emails to all borrowers whose books are due within 12 hours
        [HttpPost]
        public async Task<IActionResult> SendReminder()
        {
            var dueCutoff = DateTime.Now.AddHours(12);
            var dueReservations = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" && r.DueDate <= dueCutoff && !r.ReminderSent)
                .ToListAsync();

            int sent = 0;
            foreach (var r in dueReservations)
            {
                if (r.Book == null || r.User == null) continue;

                bool notified = false;

                // Send to Microsoft/Outlook account
                var outlookEmail = !string.IsNullOrEmpty(r.User.OutlookEmail)
                    ? r.User.OutlookEmail
                    : r.User.Email;

                if (!string.IsNullOrEmpty(outlookEmail))
                {
                    try
                    {
                        await _emailService.SendReturnReminderAsync(
                            outlookEmail, r.User.FirstName, r.Book.Title, r.DueDate!.Value);
                        notified = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[REMINDER EMAIL ERROR] {outlookEmail}: {ex.Message}");
                    }
                }

                // Send SMS
                if (!string.IsNullOrEmpty(r.User.PhoneNumber))
                {
                    try
                    {
                        await _smsService.SendReturnReminderAsync(
                            r.User.PhoneNumber, r.Book.Title, r.DueDate!.Value);
                        notified = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[REMINDER SMS ERROR] {r.User.PhoneNumber}: {ex.Message}");
                    }
                }

                if (notified)
                {
                    r.ReminderSent = true;
                    sent++;
                }
            }

            await _context.SaveChangesAsync();
            TempData["Success"] = sent > 0
                ? $"Reminder sent to {sent} borrower(s)."
                : "No new reminders to send (all already notified or no books due).";
            return RedirectToAction("Index");
        }

        // RFID tap for walk-in borrow — returns user info to auto-fill the borrow form
        [HttpGet]
        public async Task<IActionResult> FindUserByRfid(string rfid)
        {
            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfid);
            if (user == null)
                return Json(new { found = false, message = "RFID card not registered." });

            if (!user.IsActive || string.IsNullOrEmpty(user.Section))
                return Json(new { found = false, message = "Your account is still not activated. Please activate it with the librarian." });

            var sectionRecord = await _context.Sections
                .FirstOrDefaultAsync(s => s.SectionName == user.Section);

            return Json(new
            {
                found = true,
                userId = user.Id,
                studentNumber = user.StudentNumber ?? user.EmployeeNumber,
                firstName = user.FirstName,
                lastName = user.LastName,
                middleName = user.MiddleName ?? "",
                section = user.Section ?? "",
                adviserName = sectionRecord?.AdviserName ?? "",
                userType = user.UserType
            });
        }

        // RFID tap at librarian desk — finds student's pending reservation
        [HttpGet]
        public async Task<IActionResult> FindByRfid(string rfid)
        {
            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfid);
            if (user == null)
                return Json(new { found = false, message = "RFID card not registered." });
            if (!user.IsActive)
                return Json(new { found = false, message = "This account has been deactivated. Please contact MIS." });

            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == user.Id && r.Status == "Pending" && r.PickupDeadline >= DateTime.Now)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            if (reservation == null)
                return Json(new { found = false, message = $"No active reservation found for {user.FirstName} {user.LastName}." });

            var timeLeft = reservation.PickupDeadline - DateTime.Now;

            return Json(new
            {
                found = true,
                reservationId = reservation.Id,
                studentNumber = user.StudentNumber ?? user.EmployeeNumber,
                fullName = $"{user.LastName}, {user.FirstName} {(string.IsNullOrEmpty(user.MiddleName) ? "" : user.MiddleName[0] + ".")}",
                section = user.Section,
                course = user.Course,
                level = user.Level,
                bookTitle = reservation.Book?.Title,
                bookAuthor = reservation.Book?.Author,
                reservedAt = reservation.CreatedAt.ToString("MMM dd, hh:mm tt"),
                timeLeft = $"{(int)timeLeft.TotalHours}h {timeLeft.Minutes:D2}m"
            });
        }

        // Student places an online reservation
        [HttpPost]
        public async Task<IActionResult> Reserve(int bookId)
        {
            var userId = _userManager.GetUserId(User);
            var book = await _context.Books.FindAsync(bookId);

            if (book == null || book.AvailableQuantity <= 0)
            {
                TempData["Error"] = "Book is not available for reservation.";
                return RedirectToAction("Index", "Student");
            }

            int activeCount = await _context.BookReservations.CountAsync(r =>
                r.UserId == userId && (r.Status == "Pending" || r.Status == "PickedUp" || r.Status == "Overdue"));

            if (activeCount >= MaxBooksPerUser)
            {
                TempData["Error"] = $"You already have {MaxBooksPerUser} active reservations or borrowed books.";
                return RedirectToAction("Index", "Student");
            }

            _context.BookReservations.Add(new BookReservation
            {
                UserId = userId!,
                BookId = bookId,
                Status = "Pending",
                PickupDeadline = CalculatePickupDeadline(),
                ReservationDate = DateTime.Now
            });

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Reservation placed. Please pick up the book within {ReservationWindowHours} hours.";
            return RedirectToAction("Index", "Student");
        }

        // Detail view
        public async Task<IActionResult> Detail(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();
            return View(reservation);
        }

        // Grant reservation — student is physically present, hand over the book
        [HttpPost]
        public async Task<IActionResult> Approve(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            // Check 3-hour window
            if (reservation.PickupDeadline < DateTime.Now)
            {
                reservation.Status = "Void";
                reservation.LibrarianRemarks = "Auto-voided: not picked up within 3 hours.";
                await _context.SaveChangesAsync();
                TempData["Error"] = "Reservation has expired (3-hour window passed). It has been voided.";
                return RedirectToAction("Index");
            }

            int activeCount = await _context.BookReservations.CountAsync(r =>
                r.UserId == reservation.UserId &&
                (r.Status == "PickedUp" || r.Status == "Overdue") &&
                r.Id != id);

            if (activeCount >= MaxBooksPerUser)
            {
                TempData["Error"] = $"User already has {MaxBooksPerUser} books borrowed.";
                return RedirectToAction("Index");
            }

            // Grant = book is handed to student now
            reservation.Status = "PickedUp";
            reservation.ActualPickupTime = DateTime.Now;
            reservation.DueDate = CalculateDueDate();
            reservation.ReminderSent = false;

            if (reservation.Book != null)
                reservation.Book.AvailableQuantity = Math.Max(0, reservation.Book.AvailableQuantity - 1);

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "PickedUp", book = reservation.Book?.Title, user = reservation.UserId }, reservation.UserId);
            TempData["Success"] = $"Book granted. Due date: {reservation.DueDate:MMM dd, yyyy}.";
            return RedirectToAction("Index");
        }

        // Deny reservation
        [HttpPost]
        public async Task<IActionResult> Deny(int id, string? remarks)
        {
            var reservation = await _context.BookReservations.FindAsync(id);
            if (reservation == null) return NotFound();

            reservation.Status = "Denied";
            reservation.LibrarianRemarks = remarks ?? "";

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "Denied" }, reservation.UserId);
            TempData["Success"] = "Reservation denied.";
            return RedirectToAction("Index");
        }

        // Confirm pickup — student physically collected the book
        [HttpPost]
        public async Task<IActionResult> ConfirmPickup(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            reservation.Status = "PickedUp";
            reservation.ActualPickupTime = DateTime.Now;
            reservation.DueDate = CalculateDueDate();
            reservation.ReminderSent = false;

            // Decrement available quantity
            if (reservation.Book != null)
                reservation.Book.AvailableQuantity = Math.Max(0, reservation.Book.AvailableQuantity - 1);

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "PickedUp", book = reservation.Book?.Title }, reservation.UserId);
            TempData["Success"] = $"Book picked up. Due date: {reservation.DueDate:MMM dd, yyyy}.";
            return RedirectToAction("Index", new { tab = "Active" });
        }

        // Confirm return
        [HttpPost]
        public async Task<IActionResult> ConfirmReturn(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            reservation.Status = reservation.Status == "Overdue" ? "ReturnedLate" : "Returned";
            reservation.ActualReturnDate = DateTime.Now;

            if (reservation.Book != null)
                reservation.Book.AvailableQuantity = Math.Min(reservation.Book.TotalQuantity, reservation.Book.AvailableQuantity + 1);

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "Returned", book = reservation.Book?.Title }, reservation.UserId);
            TempData["Success"] = reservation.Status == "ReturnedLate"
                ? "Book returned late. The record will be cleared after 3 days."
                : "Book returned successfully.";
            return RedirectToAction("Index", new { tab = "Active" });
        }

        // Acknowledge late return — librarian dismisses the ReturnedLate record
        [HttpPost]
        public async Task<IActionResult> AcknowledgeLateReturn(int id)
        {
            var reservation = await _context.BookReservations.FindAsync(id);
            if (reservation == null) return NotFound();

            reservation.Status = "Returned";
            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "Returned" }, reservation.UserId);
            TempData["Success"] = "Late return acknowledged and record cleared.";
            return RedirectToAction("Index");
        }
    }
}
