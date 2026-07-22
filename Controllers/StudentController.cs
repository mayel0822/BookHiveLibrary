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
    public class StudentController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<LibraryHub> _hub;

        private static readonly TimeSpan LibraryOpenTime  = new TimeSpan(8, 0, 0);
        private const int ReservationWindowHours = 3;

        private static DateTime CalculatePickupDeadline()
        {
            var now = DateTime.Now;
            var openToday = DateTime.Today + LibraryOpenTime;
            var start = now < openToday ? openToday : now;
            return start.AddHours(ReservationWindowHours);
        }

        public StudentController(UserManager<ApplicationUser> userManager, ApplicationDbContext context, IHubContext<LibraryHub> hub)
        {
            _userManager = userManager;
            _context = context;
            _hub = hub;
        }

        public async Task<IActionResult> Dashboard()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            ViewBag.AvailableComputers = await _context.ComputerUnits
                .Where(c => c.IsAvailable && !c.IsArchived).CountAsync();

            var borrowedList = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == user.Id && r.Status == "PickedUp")
                .OrderByDescending(r => r.CreatedAt).ToListAsync();
            ViewBag.BorrowedBooks     = borrowedList.Count;
            ViewBag.BorrowedBooksList = borrowedList;

            ViewBag.MyReservations = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == user.Id && (r.Status == "Pending" || r.Status == "Approved"))
                .OrderByDescending(r => r.CreatedAt).Take(3).ToListAsync();

            ViewBag.BooksToReturn = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == user.Id && r.Status == "PickedUp")
                .OrderBy(r => r.DueDate).Take(3).ToListAsync();

            ViewBag.RecentBooks = await _context.Books
                .Where(b => !b.IsArchived)
                .OrderByDescending(b => b.CreatedAt).Take(8).ToListAsync();

            return View(user);
        }

        public async Task<IActionResult> BookDetail(int id)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            var book = await _context.Books.FindAsync(id);
            if (book == null || book.IsArchived) return NotFound();

            return View(book);
        }

        public async Task<IActionResult> BookViewing(string? search, string? category)
        {
            var user = await _userManager.GetUserAsync(User);

            var query = _context.Books.Where(b => !b.IsArchived).AsQueryable();

            if (!string.IsNullOrEmpty(search))
                query = query.Where(b => b.Title.Contains(search) || b.Author.Contains(search));
            if (!string.IsNullOrEmpty(category))
                query = query.Where(b => b.Category == category);

            ViewBag.Books = await query.OrderBy(b => b.Title).ToListAsync();
            ViewBag.Categories = await _context.Books
                .Where(b => !b.IsArchived && !string.IsNullOrEmpty(b.Category))
                .Select(b => b.Category).Distinct().OrderBy(c => c).ToListAsync();
            ViewBag.Search = search ?? "";
            ViewBag.Category = category ?? "";

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reserve(int bookId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            if (string.IsNullOrEmpty(user.Section))
            {
                TempData["Error"] = "You must be assigned to a section before making a reservation. Please contact the librarian.";
                return RedirectToAction("BookViewing");
            }

            if (string.IsNullOrEmpty(user.PhoneNumber))
            {
                TempData["Error"] = "You must have a phone number on your profile before making a reservation. Please update your profile.";
                return RedirectToAction("BookViewing");
            }

            var book = await _context.Books.FindAsync(bookId);
            if (book == null || book.AvailableQuantity <= 0)
            {
                TempData["Error"] = "Book is not available for reservation.";
                return RedirectToAction("BookViewing");
            }

            if (book.IsRoomUseOnly)
            {
                TempData["Error"] = "This book is for room use only and cannot be borrowed outside the library.";
                return RedirectToAction("BookViewing");
            }

            var roles = await _userManager.GetRolesAsync(user);
            if (book.BookFor == "Professor" && !roles.Contains("Professor"))
            {
                TempData["Error"] = "This book is for professors only and cannot be reserved by students.";
                return RedirectToAction("BookViewing");
            }

            var existing = await _context.BookReservations
                .Where(r => r.UserId == user.Id && r.BookId == bookId
                    && (r.Status == "Pending" || r.Status == "Approved" || r.Status == "PickedUp"))
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                var msg = existing.Status == "PickedUp"
                    ? "You already have a borrowed copy of this book. Please return it before reserving again."
                    : "You already have an active reservation for this book.";
                TempData["Error"] = msg;
                return RedirectToAction("BookViewing");
            }

            _context.BookReservations.Add(new BookReservation
            {
                UserId = user.Id,
                BookId = bookId,
                ReservationDate = DateTime.Now,
                PickupDeadline = CalculatePickupDeadline(),
                Status = "Pending",
                CreatedAt = DateTime.Now
            });
            await _context.SaveChangesAsync();

            // Push real-time notification to all librarians
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            var notifPayload = new {
                user = $"{user.FirstName} {user.LastName}",
                userType = user.UserType,
                book = book.Title,
                createdAt = DateTime.Now.ToString("MMM d, h:mm tt")
            };
            foreach (var librarian in librarians)
                await _hub.Clients.Group($"user-{librarian.Id}").SendAsync("NewReservation", notifPayload);

            TempData["Success"] = "Reservation submitted! Wait for librarian approval.";
            return RedirectToAction("BookViewing");
        }

        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");
            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> Profile(string firstName, string lastName, string middleName, string phoneNumber, IFormFile? profilePicture)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToAction("Login", "Account");

            user.FirstName   = firstName ?? user.FirstName;
            user.LastName    = lastName ?? user.LastName;
            user.MiddleName  = middleName ?? user.MiddleName;
            user.PhoneNumber = phoneNumber ?? user.PhoneNumber;

            if (profilePicture != null && profilePicture.Length > 0)
            {
                using var ms = new MemoryStream();
                await profilePicture.CopyToAsync(ms);
                var base64 = Convert.ToBase64String(ms.ToArray());
                var mimeType = profilePicture.ContentType;
                user.ProfilePicture = $"data:{mimeType};base64,{base64}";
            }

            await _userManager.UpdateAsync(user);
            TempData["Success"] = "Profile updated successfully.";
            return RedirectToAction("Profile");
        }

        public async Task<IActionResult> ComputerVacancy()
        {
            var user = await _userManager.GetUserAsync(User);

            var computers = await _context.ComputerUnits
                .Where(c => !c.IsArchived)
                .OrderBy(c => c.ComputerNumber).ToListAsync();

            ViewBag.Computers = computers;
            ViewBag.AvailableCount = computers.Count(c => c.IsAvailable);
            ViewBag.OccupiedCount = computers.Count(c => !c.IsAvailable);

            return View(user);
        }
    }
}
