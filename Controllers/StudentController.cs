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
    // This controller handles everything the student (and professor) sees:
    // their dashboard, the book catalog, reserving books, their profile, and the computer vacancy page.
    // Professors also use this same controller because their dashboard looks the same as students.
    [Authorize]
    public class StudentController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<LibraryHub> _hub; // Used to send live updates to librarian screens

        // Library hours and reservation rules
        private static readonly TimeSpan LibraryOpenTime    = new TimeSpan(8, 0, 0);   // 8:00 AM
        private static readonly TimeSpan LibraryCloseTime   = new TimeSpan(17, 0, 0);  // 5:00 PM
        private static readonly TimeSpan ReservationCutoff  = TimeSpan.FromHours(2);   // Reservations close 2 hours before library closes
        private const int ReservationWindowHours = 3; // Students have 3 hours to pick up a reserved book

        public StudentController(UserManager<ApplicationUser> userManager, ApplicationDbContext context, IHubContext<LibraryHub> hub)
        {
            _userManager = userManager;
            _context     = context;
            _hub         = hub;
        }

        // Figures out the deadline by which a student must pick up their reserved book.
        // - If it's before the library opens: deadline = library open time + 3 hours
        // - If it's during library hours: deadline = right now + 3 hours (but never past 5 PM)
        // - If it's already past closing time: deadline = 5 PM the next day
        private static DateTime CalculatePickupDeadline()
        {
            DateTime now          = DateTime.Now;
            DateTime openToday    = DateTime.Today + LibraryOpenTime;
            DateTime closeToday   = DateTime.Today + LibraryCloseTime;

            // Start counting from library open time if we're before hours
            DateTime countFrom    = now < openToday ? openToday : now;
            DateTime deadline     = countFrom.AddHours(ReservationWindowHours);

            // Deadline can't go past 5 PM
            if (deadline > closeToday)
                deadline = closeToday;

            // If the library is already closed today, deadline moves to closing time tomorrow
            bool libraryIsClosed = now >= closeToday;
            if (libraryIsClosed)
                deadline = DateTime.Today.AddDays(1) + LibraryCloseTime;

            return deadline;
        }

        // Shows the student's main dashboard page.
        // It loads how many computers are free, what books they currently have borrowed,
        // any pending reservations, books they need to return soon, and new arrivals.
        public async Task<IActionResult> Dashboard()
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return RedirectToAction("Login", "Account");

            // Count how many computers are available right now
            int availableComputers = await _context.ComputerUnits
                .Where(computer => computer.IsAvailable && !computer.IsArchived)
                .CountAsync();
            ViewBag.AvailableComputers = availableComputers;

            // Books the student currently has in their possession (status = PickedUp)
            var borrowedBooks = await _context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.UserId == student.Id && reservation.Status == "PickedUp")
                .OrderByDescending(reservation => reservation.CreatedAt)
                .ToListAsync();

            ViewBag.BorrowedBooks     = borrowedBooks.Count;
            ViewBag.BorrowedBooksList = borrowedBooks;

            // Reservations they placed but haven't picked up yet
            var pendingReservations = await _context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.UserId == student.Id
                    && (reservation.Status == "Pending" || reservation.Status == "Approved"))
                .OrderByDescending(reservation => reservation.CreatedAt)
                .Take(3)
                .ToListAsync();
            ViewBag.MyReservations = pendingReservations;

            // Books they need to return — sorted by due date so the most urgent shows first
            var booksToReturn = await _context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.UserId == student.Id && reservation.Status == "PickedUp")
                .OrderBy(reservation => reservation.DueDate)
                .Take(3)
                .ToListAsync();
            ViewBag.BooksToReturn = booksToReturn;

            // Newest books added to the library
            var recentBooks = await _context.Books
                .Where(book => !book.IsArchived)
                .OrderByDescending(book => book.CreatedAt)
                .Take(8)
                .ToListAsync();
            ViewBag.RecentBooks = recentBooks;

            return View(student);
        }

        // Shows the detail page for one book (title, description, availability, etc.)
        public async Task<IActionResult> BookDetail(int id)
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return RedirectToAction("Login", "Account");

            var book = await _context.Books.FindAsync(id);
            if (book == null || book.IsArchived) return NotFound();

            // Compute real available copies using the same rule as librarian stat cards:
            // Available = TotalQuantity - copies actually PickedUp or Overdue for this book
            int borrowedCopies = await _context.BookReservations.CountAsync(r =>
                r.BookId == id &&
                (r.Status == "PickedUp" || r.Status == "Overdue"));
            ViewBag.RealAvailable = Math.Max(0, book.TotalQuantity - borrowedCopies);

            return View(book);
        }

        // Shows the book catalog where students can browse and search for books.
        // They can search by title or author, and filter by category.
        public async Task<IActionResult> BookViewing(string? search, string? category)
        {
            var student = await _userManager.GetUserAsync(User);

            // Start with all active (non-archived) books
            var query = _context.Books.Where(book => !book.IsArchived).AsQueryable();

            // Apply search filter if the student typed something
            bool hasSearch   = !string.IsNullOrEmpty(search);
            bool hasCategory = !string.IsNullOrEmpty(category);

            if (hasSearch)
                query = query.Where(book => book.Title.Contains(search!) || book.Author.Contains(search!));

            if (hasCategory)
                query = query.Where(book => book.Category == category);

            var books = await query.OrderBy(book => book.Title).ToListAsync();

            var categories = await _context.Books
                .Where(book => !book.IsArchived && !string.IsNullOrEmpty(book.Category))
                .Select(book => book.Category)
                .Distinct()
                .OrderBy(cat => cat)
                .ToListAsync();

            ViewBag.Books      = books;
            ViewBag.Categories = categories;
            ViewBag.Search     = search ?? "";
            ViewBag.Category   = category ?? "";

            return View(student);
        }

        // Called when a student clicks "Reserve" on a book.
        // Before placing the reservation, it runs 7 checks to make sure it's allowed.
        // The "returnToDetail" flag tells us where to send the student back after:
        //   true = go back to the book detail page, false = go back to the catalog
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reserve(int bookId, bool returnToDetail = false)
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return RedirectToAction("Login", "Account");

            // Helper to redirect back to where the student came from
            IActionResult RedirectBack(bool toDetail) =>
                toDetail
                    ? RedirectToAction("BookDetail", new { id = bookId })
                    : RedirectToAction("BookViewing");

            // Check 1: The student must be assigned to a section.
            // The librarian must assign a section before a student can reserve.
            bool hasSection = !string.IsNullOrEmpty(student.Section);
            if (!hasSection)
            {
                TempData["Error"] = "You must be assigned to a section before making a reservation. Please contact the librarian.";
                return RedirectBack(returnToDetail);
            }

            // Check 2: The student must have a phone number.
            // This is needed so the library can send them SMS reminders.
            bool hasPhone = !string.IsNullOrEmpty(student.PhoneNumber);
            if (!hasPhone)
            {
                TempData["Error"] = "You must have a phone number on your profile before making a reservation. Please update your profile.";
                return RedirectBack(returnToDetail);
            }

            // Check 3: Reservations close at 3:00 PM (2 hours before the library closes at 5 PM).
            // This gives the librarian enough time to prepare and hand over books.
            DateTime reservationCutoffTime = DateTime.Today + LibraryCloseTime - ReservationCutoff;
            bool reservationsAreClosed = DateTime.Now >= reservationCutoffTime;
            if (reservationsAreClosed)
            {
                TempData["Error"] = "Reservations are closed after 3:00 PM. Please come back the next library day.";
                return RedirectBack(returnToDetail);
            }

            var book = await _context.Books.FindAsync(bookId);

            // Check 4: The book must still have copies available.
            bool bookIsAvailable = book != null && book.AvailableQuantity > 0;
            if (!bookIsAvailable)
            {
                TempData["Error"] = "Book is not available for reservation.";
                return RedirectBack(returnToDetail);
            }

            // Check 5: Some books are for reading inside the library only, not for taking home.
            if (book!.IsRoomUseOnly)
            {
                TempData["Error"] = "This book is for room use only and cannot be borrowed outside the library.";
                return RedirectBack(returnToDetail);
            }

            // Check 6: Some books are marked as professors-only and students cannot reserve them.
            var studentRoles = await _userManager.GetRolesAsync(student);
            bool bookIsProfessorOnly = book.BookFor == "Professor";
            bool studentIsProfessor  = studentRoles.Contains("Professor");
            if (bookIsProfessorOnly && !studentIsProfessor)
            {
                TempData["Error"] = "This book is for professors only and cannot be reserved by students.";
                return RedirectBack(returnToDetail);
            }

            var activeStatuses = new[] { "Pending", "Approved", "PickedUp" };

            // Check 7: cap active reservations at 3 total — matches the librarian's
            // walk-in borrow limit (BorrowController.MaxBooksPerUser). A reservation
            // still counts toward this even before pickup, since once picked up it
            // becomes a borrow; letting reservations stack unbounded would let a
            // student sidestep the same 3-book policy just by reserving online instead.
            const int MaxActiveReservations = 3;
            int activeReservationCount = await _context.BookReservations
                .CountAsync(reservation => reservation.UserId == student.Id
                    && activeStatuses.Contains(reservation.Status));
            if (activeReservationCount >= MaxActiveReservations)
            {
                TempData["Error"] = $"You already have {MaxActiveReservations} active reservations. " +
                    "Please pick up or return a book before reserving another.";
                return RedirectBack(returnToDetail);
            }

            // Check 8: the student shouldn't be able to reserve the same book twice.
            var existingReservation = await _context.BookReservations
                .Where(reservation => reservation.UserId == student.Id
                    && reservation.BookId == bookId
                    && activeStatuses.Contains(reservation.Status))
                .FirstOrDefaultAsync();

            if (existingReservation != null)
            {
                bool alreadyBorrowed = existingReservation.Status == "PickedUp";
                string errorMessage  = alreadyBorrowed
                    ? "You already have a borrowed copy of this book. Please return it before reserving again."
                    : "You already have a pending reservation for this book. Please visit the library to pick it up.";
                TempData["Error"] = errorMessage;
                return RedirectBack(returnToDetail);
            }

            // All checks passed — save the reservation to the database
            var newReservation = new BookReservation
            {
                UserId          = student.Id,
                BookId          = bookId,
                ReservationDate = DateTime.Now,
                PickupDeadline  = CalculatePickupDeadline(),
                Status          = "Pending",
                CreatedAt       = DateTime.Now
            };
            _context.BookReservations.Add(newReservation);
            await _context.SaveChangesAsync();

            // Tell all librarian screens about this new reservation in real time
            var allLibrarians = await _userManager.GetUsersInRoleAsync("Librarian");
            var notificationPayload = new
            {
                user      = $"{student.FirstName} {student.LastName}",
                userType  = student.UserType,
                book      = book.Title,
                createdAt = DateTime.Now.ToString("MMM d, h:mm tt")
            };
            foreach (var librarian in allLibrarians)
                await _hub.Clients.Group($"user-{librarian.Id}").SendAsync("NewReservation", notificationPayload);

            TempData["Success"] = "Reservation submitted! Please visit the library to pick up your book.";
            return RedirectBack(returnToDetail);
        }

        // Shows the student's own profile page
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return RedirectToAction("Login", "Account");
            return View(student);
        }

        // Saves changes the student made to their profile (name, phone number, profile picture).
        // The profile picture is converted to a text string (base64) and saved in the database
        // so we don't need a separate file storage service.
        [HttpPost]
        public async Task<IActionResult> Profile(string firstName, string lastName, string middleName, string phoneNumber, IFormFile? profilePicture)
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return RedirectToAction("Login", "Account");

            student.FirstName   = firstName   ?? student.FirstName;
            student.LastName    = lastName    ?? student.LastName;
            student.MiddleName  = middleName  ?? student.MiddleName;
            student.PhoneNumber = phoneNumber ?? student.PhoneNumber;

            bool pictureUploaded = profilePicture != null && profilePicture.Length > 0;
            if (pictureUploaded)
            {
                using var memoryStream = new MemoryStream();
                await profilePicture!.CopyToAsync(memoryStream);

                // Convert the image file to a base64 text string so it can be stored in the database
                string base64Image   = Convert.ToBase64String(memoryStream.ToArray());
                string mimeType      = profilePicture.ContentType;
                student.ProfilePicture = $"data:{mimeType};base64,{base64Image}";
            }

            await _userManager.UpdateAsync(student);
            TempData["Success"] = "Profile updated successfully.";
            return RedirectToAction("Profile");
        }

        // Returns notifications for the student's notification bell icon.
        // Includes: books waiting to be picked up, and books due back soon (within 3 days).
        public async Task<IActionResult> GetNotifications()
        {
            var student = await _userManager.GetUserAsync(User);
            if (student == null) return Json(new { count = 0, items = Array.Empty<object>() });

            // Reservations the student placed that are still waiting at the library
            var pendingReservations = await _context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.UserId == student.Id && reservation.Status == "Pending")
                .OrderByDescending(reservation => reservation.CreatedAt)
                .Take(10)
                .ToListAsync();

            // Books they currently have borrowed that are due back within 3 days.
            // NOTE: this previously checked Status == "Borrowed", a value nothing in
            // the app ever sets (the real in-hand statuses are "PickedUp"/"Overdue" —
            // see BookReservation.Status) — so this query never matched anything and
            // "due soon" reminders never actually appeared here.
            DateTime threeDaysFromNow = DateTime.Now.AddDays(3);
            var booksDueSoon = await _context.BookReservations
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.UserId == student.Id
                    && (reservation.Status == "PickedUp" || reservation.Status == "Overdue")
                    && reservation.DueDate != null
                    && reservation.DueDate <= threeDaysFromNow)
                .OrderBy(reservation => reservation.DueDate)
                .Take(5)
                .ToListAsync();

            // Build the notification items list from both lists
            var reservationItems = pendingReservations.Select(reservation => (object)new
            {
                type    = "reservation",
                title   = reservation.Book?.Title ?? "Book",
                message = "Your reservation is pending pickup.",
                time    = "Pickup by " + reservation.PickupDeadline.ToString("hh:mm tt, MMM dd"),
                label   = "Pending"
            });

            var dueItems = booksDueSoon.Select(reservation => (object)new
            {
                type    = "due",
                title   = reservation.Book?.Title ?? "Book",
                message = reservation.DueDate < DateTime.Now ? "This book is overdue!" : "Due soon.",
                time    = reservation.DueDate.HasValue ? reservation.DueDate.Value.ToString("MMM dd, yyyy") : "",
                label   = reservation.DueDate < DateTime.Now ? "Overdue" : "Due Soon"
            });

            var allNotifications = reservationItems.Concat(dueItems).ToList();

            return Json(new { count = allNotifications.Count, items = allNotifications });
        }

        // Shows the computer lab vacancy page so students can see which computers are free
        // before walking to the lab. Live updates are handled by SignalR in the background.
        public async Task<IActionResult> ComputerVacancy()
        {
            var student = await _userManager.GetUserAsync(User);

            var computers = await _context.ComputerUnits
                .Where(computer => !computer.IsArchived)
                .Include(computer => computer.Sessions)
                .OrderBy(computer => computer.ComputerNumber)
                .ToListAsync();

            int inUseCount       = computers.Count(c => c.Sessions.Any(s => s.IsActive));
            int availableCount   = computers.Count(c => c.IsAvailable && !c.Sessions.Any(s => s.IsActive));
            int unavailableCount = computers.Count(c => !c.IsAvailable && !c.Sessions.Any(s => s.IsActive));

            ViewBag.Computers        = computers;
            ViewBag.AvailableCount   = availableCount;
            ViewBag.InUseCount       = inUseCount;
            ViewBag.UnavailableCount = unavailableCount;

            return View(student);
        }
    }
}
