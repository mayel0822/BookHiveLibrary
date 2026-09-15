using BookHiveLibrary.Data;
using BookHiveLibrary.Helpers;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Controllers
{
    // This controller handles everything related to borrowing books at the library desk.
    // Only librarians can access this — it's the "transaction page" where they approve,
    // deny, hand over, and accept returned books.
    //
    // The full process a book goes through:
    //   Student reserves online (Pending) → Librarian approves (PickedUp) → Student returns (Returned)
    //   If not returned on time: Overdue → when returned late: ReturnedLate
    [Authorize(Roles = "LIBRARIAN")]
    public class BorrowController : Controller
    {
        private const int MaxBooksPerUser        = 3;  // A student can only borrow 3 books at a time
        private const int BorrowDays             = 2;  // Books must be returned after 2 days
        private const int ReservationWindowHours = 3;  // Students must pick up within 3 hours of reserving
        private const int LateReturnClearDays    = 3;  // ReturnedLate records are auto-cleared after 3 days
        private const int ReminderHoursAhead     = 12; // Send reminders when a book is due within 12 hours

        private static readonly TimeSpan LibraryOpenTime  = new TimeSpan(8, 0, 0);    // 8:00 AM
        private static readonly TimeSpan LibraryCloseTime = new TimeSpan(16, 30, 0);  // 4:30 PM

        // The due date is always 2 days from today at 4:30 PM Philippine time.
        // Computed in PH wall-clock time, then converted to UTC since DueDate is
        // stored in the database and compared against DateTime.UtcNow elsewhere.
        // (DateTime.Now/.Today here would silently use the SERVER's clock instead
        // of PH time — UTC on Azure, so "4:30 PM" would actually land at 4:30 AM
        // the next day in the Philippines.)
        private static DateTime CalculateDueDate()
        {
            DateTime dueDatePh = PhTime.Now.Date.AddDays(BorrowDays) + LibraryCloseTime;
            return PhTime.ToUtc(dueDatePh);
        }

        // The pickup deadline is 3 hours from now (or from library open time if it's before hours).
        // Same PH-time-then-convert-to-UTC approach as CalculateDueDate above.
        private static DateTime CalculatePickupDeadline()
        {
            DateTime nowPh     = PhTime.Now;
            DateTime openToday = nowPh.Date + LibraryOpenTime;

            // If we're before the library opens, start counting from open time
            DateTime countFrom = nowPh < openToday ? openToday : nowPh;
            DateTime deadlinePh = countFrom.AddHours(ReservationWindowHours);
            return PhTime.ToUtc(deadlinePh);
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
            _context      = context;
            _userManager  = userManager;
            _emailService = emailService;
            _smsService   = smsService;
            _hub          = hub;
        }

        // Sends a live update to all librarian screens so they see changes without refreshing the page.
        // Optionally also sends to the student's own screen if studentUserId is provided.
        private async Task PushBookEvent(string eventName, object payload, string? studentUserId = null)
        {
            var allLibrarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var librarian in allLibrarians)
                await _hub.Clients.Group($"user-{librarian.Id}").SendAsync(eventName, payload);

            bool alsoNotifyStudent = studentUserId != null;
            if (alsoNotifyStudent)
                await _hub.Clients.Group($"user-{studentUserId}").SendAsync(eventName, payload);
        }

        // Call right after a pickup decrements a book's AvailableQuantity. If that
        // pickup was the LAST copy, everyone else still waiting on a Pending
        // reservation for the same book gets notified — it's first-come-first-served
        // at pickup, not at reservation time, so a student who reserved early can
        // still lose out to someone who reserves later but arrives to collect first.
        // Without this, a waiting student has no way to know their reservation just
        // became effectively stuck until a copy is returned.
        private async Task NotifyOtherReserversIfBookJustRanOut(Book book, string pickedUpByUserId)
        {
            if (book.AvailableQuantity > 0) return; // copies remain — nobody else is affected

            var otherWaitingReservations = await _context.BookReservations
                .Where(r => r.BookId == book.Id
                    && r.Status == "Pending"
                    && r.UserId != pickedUpByUserId)
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync();

            foreach (var waitingUserId in otherWaitingReservations)
            {
                _context.StudentNotifications.Add(new StudentNotification
                {
                    UserId  = waitingUserId,
                    Type    = "Unavailable",
                    Title   = book.Title,
                    Message = "This book just ran out of copies while your reservation was pending. " +
                              "You'll still be considered once a copy is returned, but it's no longer " +
                              "guaranteed — first to arrive and pick up gets it.",
                });
            }
            // Caller is expected to SaveChangesAsync() afterward (already about to,
            // alongside the pickup itself).
        }

        // Loads the dropdowns used in the walk-in borrow form:
        //   - List of active students/professors
        //   - List of books that still have copies available
        private async Task LoadBorrowFormData()
        {
            ViewBag.Students = await _userManager.Users
                .Where(user => (user.UserType == "Student" || user.UserType == "Professor") && user.IsActive)
                .OrderBy(user => user.LastName)
                .ToListAsync();

            ViewBag.AvailableBooks = await _context.Books
                .Where(book => !book.IsArchived && book.AvailableQuantity > 0)
                .OrderBy(book => book.Title)
                .ToListAsync();
        }

        // The main book transaction page that the librarian sees.
        // Every time this page loads, it automatically does 3 cleanup tasks:
        //
        //   1. If a student reserved a book but didn't pick it up within 3 hours,
        //      the reservation is automatically cancelled (status changed to "Void")
        //
        //   2. If a book's due date has passed and the student still has it, it's marked "Overdue"
        //
        //   3. If a book was returned late but it's been 3 or more days since return,
        //      the record is cleaned up and marked "Returned" to reduce clutter
        //
        // After cleanup, it loads all the panels the librarian needs: pending reservations,
        // books due for reminders, all currently borrowed books, and the walk-in form.
        public async Task<IActionResult> Index()
        {
            // Step 1: Auto-cancel reservations where the student didn't pick up in time
            var expiredReservations = await _context.BookReservations
                .Where(reservation => reservation.Status == "Pending" && reservation.PickupDeadline < DateTime.UtcNow)
                .ToListAsync();

            foreach (var expired in expiredReservations)
            {
                expired.Status           = "Void";
                expired.LibrarianRemarks = "Auto-voided: not picked up within 3 hours.";
            }
            if (expiredReservations.Any()) await _context.SaveChangesAsync();

            // Step 2: Mark books as overdue if the due date has already passed
            var overdueBooks = await _context.BookReservations
                .Where(reservation => reservation.Status == "PickedUp" && reservation.DueDate < DateTime.UtcNow)
                .ToListAsync();

            foreach (var overdueBook in overdueBooks)
                overdueBook.Status = "Overdue";

            if (overdueBooks.Any()) await _context.SaveChangesAsync();

            // Step 3: Auto-clear "ReturnedLate" records that are older than 3 days
            DateTime lateClearCutoff = DateTime.UtcNow.AddDays(-LateReturnClearDays);
            var oldLateReturns = await _context.BookReservations
                .Where(reservation => reservation.Status == "ReturnedLate"
                    && reservation.ActualReturnDate < lateClearCutoff)
                .ToListAsync();

            foreach (var lateRecord in oldLateReturns)
                lateRecord.Status = "Returned";

            if (oldLateReturns.Any()) await _context.SaveChangesAsync();

            // Load the pending reservations panel — books waiting to be handed to students
            var pendingReservations = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "Pending")
                .OrderBy(reservation => reservation.CreatedAt)
                .ToListAsync();
            ViewBag.PendingReservations = pendingReservations;

            // Look up adviser names for each section (used in the display)
            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(section => section.SectionName, section => section.AdviserName);

            // Books due back within 12 hours that haven't received a reminder yet
            DateTime reminderCutoff = DateTime.UtcNow.AddHours(ReminderHoursAhead);
            var booksDueForReminder = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "PickedUp"
                    && reservation.DueDate <= reminderCutoff
                    && !reservation.ReminderSent)
                .OrderBy(reservation => reservation.DueDate)
                .ToListAsync();
            ViewBag.BooksDue      = booksDueForReminder;
            ViewBag.BooksDueCount = booksDueForReminder.Count;

            // All books currently out (borrowed or overdue)
            ViewBag.AllBooksDue = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "PickedUp" || reservation.Status == "Overdue")
                .OrderBy(reservation => reservation.DueDate)
                .ToListAsync();

            // The main borrowed books table
            var borrowedBooks = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation =>
                    reservation.Status == "PickedUp" ||
                    reservation.Status == "Overdue"  ||
                    reservation.Status == "Approved")
                .OrderByDescending(reservation => reservation.CreatedAt)
                .ToListAsync();

            ViewBag.PendingCount = await _context.BookReservations.CountAsync(reservation => reservation.Status == "Pending");

            // Stat card rules (same on every page):
            //   Total     = SUM(TotalQuantity) of non-archived books
            //   Borrowed  = physically picked up and not yet returned (PickedUp or Overdue)
            //               Approved/Pending do NOT count — student may never come → Void → still Available
            //   Available = Total − Borrowed
            ViewBag.TotalBooks     = await _context.Books.Where(b => !b.IsArchived).SumAsync(b => (int?)b.TotalQuantity) ?? 0;
            ViewBag.BorrowedCount  = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp" || r.Status == "Overdue");
            ViewBag.AvailableCount = (int)ViewBag.TotalBooks - (int)ViewBag.BorrowedCount;

            await LoadBorrowFormData();
            return View(borrowedBooks);
        }

        // Called when a librarian manually creates a borrow record for a walk-in student.
        // (The student didn't reserve online — they just walked up to the desk.)
        // Checks that:
        //   - The student and book both exist
        //   - The student hasn't already borrowed 3 books
        //   - The book has copies available
        //   - The book is not a "room use only" book
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateBorrow(string userId, int bookId)
        {
            var student = await _userManager.FindByIdAsync(userId);
            var book    = await _context.Books.FindAsync(bookId);

            if (student == null || book == null)
            {
                TempData["Error"] = "Invalid user or book.";
                return RedirectToAction("Index");
            }

            int currentlyBorrowed = await _context.BookReservations.CountAsync(reservation =>
                reservation.UserId == userId
                && (reservation.Status == "PickedUp" || reservation.Status == "Overdue"));

            if (currentlyBorrowed >= MaxBooksPerUser)
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

            // Reduce the available count by 1 since one copy is now being taken
            book.AvailableQuantity -= 1;
            await NotifyOtherReserversIfBookJustRanOut(book, userId);

            var newBorrow = new BookReservation
            {
                UserId           = userId,
                BookId           = bookId,
                Status           = "PickedUp", // Already in the student's hands
                ActualPickupTime = DateTime.UtcNow,
                DueDate          = CalculateDueDate(),
                PickupDeadline   = DateTime.UtcNow
            };
            _context.BookReservations.Add(newBorrow);

            await _context.SaveChangesAsync();

            string studentName = $"{student.FirstName} {student.LastName}";
            await PushBookEvent("BookTransactionUpdated",
                new { action = "PickedUp", book = book.Title, user = studentName }, userId);

            // Broadcast to every logged-in student/professor (not just this one) so
            // anyone browsing the catalog sees the copy count update, instead of the
            // book staying stuck at "Available" until they happen to refresh.
            await _hub.Clients.Group("students").SendAsync("BookAvailabilityChanged",
                new { bookId = book.Id, availableQuantity = book.AvailableQuantity });

            TempData["Success"] = $"Book \"{book.Title}\" borrowed by {studentName}. Due in {BorrowDays} days.";
            return RedirectToAction("Index");
        }

        // Sends email and SMS reminders to students whose books are due back within 12 hours.
        // Only sends to students who haven't received a reminder yet (ReminderSent is false).
        // After sending, marks ReminderSent = true so we don't send again on the next click.
        // Uses the student's school email (OutlookEmail) if available, otherwise their regular email.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendReminder()
        {
            DateTime reminderCutoff = DateTime.UtcNow.AddHours(ReminderHoursAhead);

            var dueReservations = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation =>
                    reservation.Status == "PickedUp"
                    && reservation.DueDate <= reminderCutoff
                    && !reservation.ReminderSent)
                .ToListAsync();

            int remindersSuccessfullySent = 0;

            foreach (var dueRecord in dueReservations)
            {
                if (dueRecord.Book == null || dueRecord.User == null) continue;

                bool notified = false;

                // Use their school email if they have one, otherwise use their regular email
                string? emailToUse = !string.IsNullOrEmpty(dueRecord.User.OutlookEmail)
                    ? dueRecord.User.OutlookEmail
                    : dueRecord.User.Email;

                bool hasEmailAddress = !string.IsNullOrEmpty(emailToUse);
                if (hasEmailAddress)
                {
                    try
                    {
                        await _emailService.SendReturnReminderAsync(
                            emailToUse!, dueRecord.User.FirstName, dueRecord.Book.Title, dueRecord.DueDate!.Value);
                        notified = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[REMINDER EMAIL ERROR] {emailToUse}: {ex.Message}");
                    }
                }

                bool hasPhoneNumber = !string.IsNullOrEmpty(dueRecord.User.PhoneNumber);
                if (hasPhoneNumber)
                {
                    try
                    {
                        await _smsService.SendReturnReminderAsync(
                            dueRecord.User.PhoneNumber!,
                            dueRecord.User.FirstName + " " + dueRecord.User.LastName,
                            dueRecord.Book.Title,
                            dueRecord.DueDate!.Value);
                        notified = true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[REMINDER SMS ERROR] {dueRecord.User.PhoneNumber}: {ex.Message}");
                    }
                }

                if (notified)
                {
                    dueRecord.ReminderSent = true;
                    remindersSuccessfullySent++;
                }
            }

            await _context.SaveChangesAsync();

            bool anySent = remindersSuccessfullySent > 0;
            TempData["Success"] = anySent
                ? $"Reminder sent to {remindersSuccessfullySent} borrower(s)."
                : "No new reminders to send (all already notified or no books due).";

            return RedirectToAction("Index");
        }

        // Looks up a student by their RFID card number so the librarian can fill in the borrow form quickly.
        // The student taps their card on the reader → the librarian's form automatically fills in the name and section.
        [HttpGet]
        public async Task<IActionResult> FindUserByRfid(string rfid)
        {
            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfid);

            if (user == null)
                return Json(new { found = false, message = "RFID card not registered." });

            bool accountNotReady = !user.IsActive || string.IsNullOrEmpty(user.Section);
            if (accountNotReady)
                return Json(new { found = false, message = "Your account is still not activated. Please activate it with the librarian." });

            var sectionRecord = await _context.Sections
                .FirstOrDefaultAsync(section => section.SectionName == user.Section);

            return Json(new
            {
                found         = true,
                userId        = user.Id,
                studentNumber = user.StudentNumber ?? user.EmployeeNumber,
                firstName     = user.FirstName,
                lastName      = user.LastName,
                middleName    = user.MiddleName ?? "",
                section       = user.Section ?? "",
                adviserName   = sectionRecord?.AdviserName ?? "",
                userType      = user.UserType
            });
        }

        // Similar to FindUserByRfid above, but this one looks for an existing pending reservation.
        // Used on the borrow desk: a student taps their card and the librarian can immediately
        // see which book they reserved and how much time they have left to pick it up.
        [HttpGet]
        public async Task<IActionResult> FindByRfid(string rfid)
        {
            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfid);

            if (user == null)
                return Json(new { found = false, message = "RFID card not registered." });

            if (!user.IsActive)
                return Json(new { found = false, message = "This account has been deactivated. Please contact MIS." });

            // Look for a pending reservation that hasn't expired yet
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == user.Id && r.Status == "Pending" && r.PickupDeadline >= DateTime.UtcNow)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            if (reservation == null)
                return Json(new { found = false, message = $"No active reservation found for {user.FirstName} {user.LastName}." });

            TimeSpan timeLeft = reservation.PickupDeadline - DateTime.UtcNow;

            // Format the middle initial (e.g., "Santos" → "S.")
            string middleInitial = string.IsNullOrEmpty(user.MiddleName) ? "" : user.MiddleName[0] + ".";
            string fullName      = $"{user.LastName}, {user.FirstName} {middleInitial}".Trim();

            return Json(new
            {
                found         = true,
                reservationId = reservation.Id,
                studentNumber = user.StudentNumber ?? user.EmployeeNumber,
                fullName,
                section       = user.Section,
                course        = user.Course,
                level         = user.Level,
                bookTitle     = reservation.Book?.Title,
                bookAuthor    = reservation.Book?.Author,
                reservedAt    = PhTime.FromUtc(reservation.CreatedAt).ToString("MMM dd, hh:mm tt"),
                timeLeft      = $"{(int)timeLeft.TotalHours}h {timeLeft.Minutes:D2}m"
            });
        }

        // Lets a student place an online reservation (also accessible from the student side).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reserve(int bookId)
        {
            string? userId = _userManager.GetUserId(User);
            var book       = await _context.Books.FindAsync(bookId);

            bool bookIsAvailable = book != null && book.AvailableQuantity > 0;
            if (!bookIsAvailable)
            {
                TempData["Error"] = "Book is not available for reservation.";
                return RedirectToAction("Index", "Student");
            }

            int activeReservations = await _context.BookReservations.CountAsync(reservation =>
                reservation.UserId == userId
                && (reservation.Status == "Pending" || reservation.Status == "PickedUp" || reservation.Status == "Overdue"));

            if (activeReservations >= MaxBooksPerUser)
            {
                TempData["Error"] = $"You already have {MaxBooksPerUser} active reservations or borrowed books.";
                return RedirectToAction("Index", "Student");
            }

            var newReservation = new BookReservation
            {
                UserId          = userId!,
                BookId          = bookId,
                Status          = "Pending",
                PickupDeadline  = CalculatePickupDeadline(),
                ReservationDate = DateTime.UtcNow
            };
            _context.BookReservations.Add(newReservation);

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Reservation placed. Please pick up the book within {ReservationWindowHours} hours.";
            return RedirectToAction("Index", "Student");
        }

        // Shows the detail page for a specific reservation
        public async Task<IActionResult> Detail(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();
            return View(reservation);
        }

        // The student is at the desk and the librarian is handing over the book.
        // Before handing it over, checks:
        //   - The 3-hour pickup window hasn't expired (if it did, cancels the reservation)
        //   - The student hasn't already borrowed 3 books
        // Sets the book status to "PickedUp" and records the pickup time and due date.
        // Reduces the available copy count by 1.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            // If the student waited too long, automatically cancel the reservation
            bool pickupWindowExpired = reservation.PickupDeadline < DateTime.UtcNow;
            if (pickupWindowExpired)
            {
                reservation.Status           = "Void";
                reservation.LibrarianRemarks = "Auto-voided: not picked up within 3 hours.";
                await _context.SaveChangesAsync();
                TempData["Error"] = "Reservation has expired (3-hour window passed). It has been voided.";
                return RedirectToAction("Index");
            }

            int otherBorrowedBooks = await _context.BookReservations.CountAsync(r =>
                r.UserId == reservation.UserId
                && (r.Status == "PickedUp" || r.Status == "Overdue")
                && r.Id != id);

            if (otherBorrowedBooks >= MaxBooksPerUser)
            {
                TempData["Error"] = $"User already has {MaxBooksPerUser} books borrowed.";
                return RedirectToAction("Index");
            }

            reservation.Status           = "PickedUp";
            reservation.ActualPickupTime = DateTime.UtcNow;
            reservation.DueDate          = CalculateDueDate();
            reservation.ReminderSent     = false; // Reset so a reminder can still be sent later

            // Subtract one available copy since the student is taking it
            if (reservation.Book != null)
            {
                int newAvailableCount               = reservation.Book.AvailableQuantity - 1;
                reservation.Book.AvailableQuantity  = Math.Max(0, newAvailableCount);
                await NotifyOtherReserversIfBookJustRanOut(reservation.Book, reservation.UserId);
            }

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated",
                new { action = "PickedUp", book = reservation.Book?.Title, user = reservation.UserId }, reservation.UserId);

            if (reservation.Book != null)
            {
                await _hub.Clients.Group("students").SendAsync("BookAvailabilityChanged",
                    new { bookId = reservation.Book.Id, availableQuantity = reservation.Book.AvailableQuantity });
            }

            TempData["Success"] = $"Book granted. Due date: {reservation.DueDate:MMM dd, yyyy}.";
            return RedirectToAction("Index");
        }

        // The librarian is rejecting the reservation. They can add a reason/remark.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Deny(int id, string? remarks)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (reservation == null) return NotFound();

            reservation.Status           = "Denied";
            reservation.LibrarianRemarks = remarks ?? "";

            // Persisted so the student actually sees this — before this, a denial
            // produced no notification at all: it's not covered by the "pending
            // pickup" or "due soon" reminders (those are for still-active
            // reservations), and the live page reload on BookTransactionUpdated
            // only helps if the student happens to be on the Dashboard at that exact
            // moment. Unlike those, this doesn't disappear once read — it just stops
            // counting toward the unread badge.
            _context.StudentNotifications.Add(new StudentNotification
            {
                UserId  = reservation.UserId,
                Type    = "Denied",
                Title   = reservation.Book?.Title ?? "Book",
                Message = string.IsNullOrWhiteSpace(remarks)
                    ? "Your reservation was denied by the librarian."
                    : $"Your reservation was denied: {remarks}",
            });

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated", new { action = "Denied" }, reservation.UserId);
            TempData["Success"] = "Reservation denied.";
            return RedirectToAction("Index");
        }

        // The student physically collected the book. Records the pickup time and due date.
        // Reduces the available copy count.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPickup(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            reservation.Status           = "PickedUp";
            reservation.ActualPickupTime = DateTime.UtcNow;
            reservation.DueDate          = CalculateDueDate();
            reservation.ReminderSent     = false;

            if (reservation.Book != null)
            {
                int newAvailableCount               = reservation.Book.AvailableQuantity - 1;
                reservation.Book.AvailableQuantity  = Math.Max(0, newAvailableCount);
                await NotifyOtherReserversIfBookJustRanOut(reservation.Book, reservation.UserId);
            }

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated",
                new { action = "PickedUp", book = reservation.Book?.Title }, reservation.UserId);

            if (reservation.Book != null)
            {
                await _hub.Clients.Group("students").SendAsync("BookAvailabilityChanged",
                    new { bookId = reservation.Book.Id, availableQuantity = reservation.Book.AvailableQuantity });
            }

            TempData["Success"] = $"Book picked up. Due date: {reservation.DueDate:MMM dd, yyyy}.";
            return RedirectToAction("Index", new { tab = "Active" });
        }

        // The student has returned the book.
        // If the book is overdue, it's marked "ReturnedLate" (and auto-clears after 3 days).
        // If returned on time, it's marked "Returned" right away.
        // Adds 1 back to the available copy count.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmReturn(int id)
        {
            var reservation = await _context.BookReservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reservation == null) return NotFound();

            bool returnedLate     = reservation.Status == "Overdue";
            reservation.Status    = returnedLate ? "ReturnedLate" : "Returned";
            reservation.ActualReturnDate = DateTime.UtcNow;

            // Add the copy back to the shelf (but never exceed the total count)
            if (reservation.Book != null)
            {
                int newAvailableCount              = reservation.Book.AvailableQuantity + 1;
                reservation.Book.AvailableQuantity = Math.Min(reservation.Book.TotalQuantity, newAvailableCount);
            }

            await _context.SaveChangesAsync();
            await PushBookEvent("BookTransactionUpdated",
                new { action = "Returned", book = reservation.Book?.Title }, reservation.UserId);

            if (reservation.Book != null)
            {
                await _hub.Clients.Group("students").SendAsync("BookAvailabilityChanged",
                    new { bookId = reservation.Book.Id, availableQuantity = reservation.Book.AvailableQuantity });
            }

            TempData["Success"] = returnedLate
                ? "Book returned late. The record will be cleared after 3 days."
                : "Book returned successfully.";

            return RedirectToAction("Index", new { tab = "Active" });
        }

        // The librarian manually clears a "ReturnedLate" record before the 3-day auto-clear.
        // Changes the status to "Returned" immediately.
        [HttpPost]
        [ValidateAntiForgeryToken]
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
