using BookHiveLibrary.Data;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using OfficeOpenXml;

namespace BookHiveLibrary.Controllers
{
    // This controller handles everything on the librarian's side of the system.
    // Only users with the LIBRARIAN role can access it.
    //
    // What it covers:
    //   - The librarian dashboard (live counters and lists)
    //   - Managing students and professors (search, activate, assign to section)
    //   - Section management (create, delete, assign students)
    //   - Viewing activity logs (library entry, computer use, book borrowing)
    //   - Exporting activity logs to Excel
    //   - The librarian's own profile page
    //   - RFID card taps from the library entrance and desk readers
    [Authorize(Roles = "LIBRARIAN")]
    public class LibrarianController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub;    // For sending live updates to screens
        private readonly IConfiguration _config;           // For reading the RFID device security key

        public LibrarianController(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
            IHubContext<LibraryHub> hub, IConfiguration config)
        {
            _context     = context;
            _userManager = userManager;
            _hub         = hub;
            _config      = config;
        }

        // Checks that the RFID device (ESP32 reader) sent the correct secret key in the request header.
        // This prevents random devices from faking RFID tap events.
        private bool IsValidDeviceKey()
        {
            string expectedKey = _config["RfidDeviceKey"] ?? "";
            string receivedKey = Request.Headers["X-Device-Key"].ToString();
            return receivedKey == expectedKey;
        }

        // The kiosk page shown on the library entrance screen.
        // No login required so it can always display even if nobody is logged in.
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public IActionResult Kiosk() => View();

        // ── Dashboard ─────────────────────────────────────────────────────────

        // The main librarian dashboard page.
        // Shows live counts: how many people are inside, books borrowed, pending reservations,
        // computers in use, and available computers.
        // Also shows the 10 most relevant records from each category as quick-access lists.
        public async Task<IActionResult> Dashboard()
        {
            ViewBag.ActiveUsers       = await _context.RFIDLogs.CountAsync(log => log.IsInside);
            ViewBag.BookBorrowed      = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp");
            ViewBag.Reservations      = await _context.BookReservations.CountAsync(r => r.Status == "Pending");
            ViewBag.ComputerInUse     = await _context.ComputerSessions.CountAsync(session => session.EndTime == null);
            ViewBag.ComputerAvailable = await _context.ComputerUnits.CountAsync(computer => !computer.IsArchived && computer.IsAvailable);

            // Currently borrowed or overdue books — sorted by due date (most urgent first)
            ViewBag.CurrentBorrowers = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "PickedUp" || reservation.Status == "Overdue")
                .OrderBy(reservation => reservation.DueDate)
                .Take(10)
                .ToListAsync();

            // Pending reservations — oldest ones first so they're served in order
            ViewBag.ReservationList = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "Pending")
                .OrderBy(reservation => reservation.CreatedAt)
                .Take(10)
                .ToListAsync();

            // Books due back within 24 hours so the librarian can prepare follow-ups
            DateTime oneDayFromNow = DateTime.Now.AddDays(1);
            ViewBag.BookDues = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "PickedUp" && reservation.DueDate <= oneDayFromNow)
                .OrderBy(reservation => reservation.DueDate)
                .Take(10)
                .ToListAsync();

            return View();
        }

        // ── User Management ──────────────────────────────────────────────────

        // Shows a list of all active students and professors.
        // The librarian can search by name, ID number, or email, and filter by role.
        // Also loads all sections for the section assignment dropdown.
        public async Task<IActionResult> UserManagement(string? search, string? userType)
        {
            var query = _userManager.Users
                .Where(user => user.IsActive && (user.UserType == "Student" || user.UserType == "Professor"));

            bool filterByType = !string.IsNullOrWhiteSpace(userType);
            if (filterByType)
                query = query.Where(user => user.UserType == userType);

            bool filterBySearch = !string.IsNullOrWhiteSpace(search);
            if (filterBySearch)
                query = query.Where(user =>
                    user.FirstName.Contains(search!)      ||
                    user.LastName.Contains(search!)       ||
                    user.StudentNumber.Contains(search!)  ||
                    user.EmployeeNumber.Contains(search!) ||
                    user.Email!.Contains(search!));

            var users = await query.OrderByDescending(user => user.CreatedAt).ToListAsync();

            ViewBag.Search   = search;
            ViewBag.UserType = userType;
            ViewBag.Sections = await _context.Sections.OrderBy(section => section.SectionName).ToListAsync();

            return View(users);
        }

        // ── Sectioning ────────────────────────────────────────────────────────

        // Shows the section management page.
        // Lists all sections grouped by level, and loads professor names for the adviser dropdown.
        public async Task<IActionResult> Sectioning()
        {
            ViewBag.Sections = await _context.Sections
                .OrderBy(section => section.Level)
                .ThenBy(section => section.SectionName)
                .ToListAsync();

            ViewBag.Professors = await _context.Users
                .Where(user => user.UserType == "Professor" && user.IsActive)
                .OrderBy(user => user.LastName).ThenBy(user => user.FirstName)
                .Select(user => user.LastName + ", " + user.FirstName)
                .ToListAsync();

            return View();
        }

        // Creates a new section.
        // Rejects the request if a section with the same name already exists (case-insensitive).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSection(string level, string course, string year, string sectionName, string adviserName)
        {
            // Duplicate check — section names must be unique
            bool alreadyExists = await _context.Sections
                .AnyAsync(s => s.SectionName.ToLower() == sectionName.ToLower());

            if (alreadyExists)
            {
                TempData["Error"] = $"A section named \"{sectionName}\" already exists. Please use a different name.";
                return RedirectToAction("Sectioning");
            }

            var newSection = new Section
            {
                Level       = level,
                Course      = course,
                Year        = year,
                SectionName = sectionName,
                AdviserName = adviserName ?? ""
            };
            _context.Sections.Add(newSection);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Section created.";
            return RedirectToAction("Sectioning");
        }

        // Deletes all sections for a given level group (Junior High, Senior High, or Tertiary).
        // IMPORTANT: When sections are deleted, all students in those sections are also deactivated
        // (their Section field is cleared and their account is set to inactive).
        // This is used at the start of a new school year to reset section assignments.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllSections(string levelGroup)
        {
            // Choose which sections to delete based on the level group
            IQueryable<Section> query = _context.Sections;

            if (levelGroup == "JHS")
                query = query.Where(section => section.Level == "Junior High School");
            else if (levelGroup == "SHS")
                query = query.Where(section => section.Level == "Senior High School");
            else if (levelGroup == "Tertiary")
                query = query.Where(section => section.Level == "Tertiary");

            var sectionsToDelete = await query.ToListAsync();
            var sectionNames     = sectionsToDelete.Select(section => section.SectionName).ToList();

            // Deactivate all students who were in the deleted sections
            var affectedStudents = await _context.Users
                .Where(user => user.UserType == "Student"
                    && user.Section != null
                    && sectionNames.Contains(user.Section))
                .ToListAsync();

            foreach (var student in affectedStudents)
            {
                student.Section  = "";
                student.IsActive = false;
            }

            _context.Sections.RemoveRange(sectionsToDelete);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"{levelGroup} sections have been reset.";
            return RedirectToAction("Sectioning");
        }

        // Deletes a single section and deactivates all students who were in it.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSection(int id)
        {
            var section = await _context.Sections.FindAsync(id);
            if (section != null)
            {
                // Deactivate all students in this section before deleting it
                var affectedStudents = await _context.Users
                    .Where(user => user.UserType == "Student" && user.Section == section.SectionName)
                    .ToListAsync();

                foreach (var student in affectedStudents)
                {
                    student.Section  = "";
                    student.IsActive = false;
                }

                _context.Sections.Remove(section);
                await _context.SaveChangesAsync();
            }
            TempData["Success"] = "Section deleted.";
            return RedirectToAction("Sectioning");
        }

        // Moves a user to a different section.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSection(string userId, string section)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.Section = section;
                await _userManager.UpdateAsync(user);
            }
            TempData["Success"] = "Section updated.";
            return RedirectToAction("UserManagement");
        }

        // Removes a user from their section (leaves them without a section assignment).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetSection(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.Section = "";
                await _userManager.UpdateAsync(user);
            }
            TempData["Success"] = "Section reset.";
            return RedirectToAction("UserManagement");
        }

        // Clears the section for ALL users of a given type (Student or Professor).
        // Used when resetting all section assignments at the start of a new school year.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetAllSections(string userType)
        {
            var allUsers = _userManager.Users.Where(user => user.UserType == userType).ToList();
            foreach (var user in allUsers)
                user.Section = "";

            foreach (var user in allUsers)
                await _userManager.UpdateAsync(user);

            TempData["Success"] = $"All {userType} sections have been reset.";
            return RedirectToAction("UserManagement");
        }

        // Turns a user account on or off.
        // When turned off, the user will be automatically logged out the next time they do anything.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAccountStatus(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.IsActive = !user.IsActive;
                await _userManager.UpdateAsync(user);
            }
            return RedirectToAction("UserManagement");
        }

        // Activates a student account and assigns them to a section at the same time.
        // Used when a student has registered via Microsoft but needs a section before they can use the system.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateWithSection(string userId, string section)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.Section  = section;
                user.IsActive = true;
                await _userManager.UpdateAsync(user);
            }
            return RedirectToAction("UserManagement");
        }

        // ── Activity Logs ─────────────────────────────────────────────────────

        // Shows activity logs with three tabs: Library entries (RFID taps), Computer sessions, Book borrowing.
        // The librarian can filter by date. Only the 100 most recent records are shown per tab.
        public async Task<IActionResult> StudentActivity(string? date, string? logType)
        {
            // Default to All if none is selected
            logType ??= "All";

            // Try to parse the date filter if one was provided
            DateTime? parsedDate = null;
            bool dateWasProvided = !string.IsNullOrWhiteSpace(date);
            if (dateWasProvided && DateTime.TryParse(date, out DateTime parsedResult))
                parsedDate = parsedResult;

            ViewBag.Date    = date;
            ViewBag.LogType = logType;

            // Helper: build UTC range from a PHT calendar day
            (DateTime utcStart, DateTime utcEnd) PhtDayToUtcRange(DateTime phtDay)
            {
                var start = TimeZoneInfo.ConvertTimeToUtc(phtDay.Date, _phZone);
                return (start, start.AddDays(1));
            }

            // ── Computer Log ──────────────────────────────────────────────────
            if (logType == "Computer")
            {
                var computerQuery = _context.ComputerSessions
                    .Include(session => session.User)
                    .Include(session => session.ComputerUnit)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    computerQuery = computerQuery.Where(session => session.StartTime.Date == parsedDate.Value.Date);

                ViewBag.ComputerSessions = await computerQuery
                    .OrderByDescending(session => session.StartTime)
                    .Take(100)
                    .ToListAsync();

                return View(new List<RFIDLog>());
            }

            // ── Book Log ──────────────────────────────────────────────────────
            if (logType == "Book")
            {
                var bookQuery = _context.BookReservations
                    .Include(reservation => reservation.User)
                    .Include(reservation => reservation.Book)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    bookQuery = bookQuery.Where(reservation => reservation.CreatedAt.Date == parsedDate.Value.Date);

                ViewBag.BookLogs = await bookQuery
                    .OrderByDescending(reservation => reservation.CreatedAt)
                    .Take(100)
                    .ToListAsync();

                return View(new List<RFIDLog>());
            }

            // ── All Logs — combine all three sources ──────────────────────────
            if (logType == "All")
            {
                // Library (RFID) logs
                var rfidQ = _context.RFIDLogs.Include(l => l.User).AsQueryable();
                if (parsedDate.HasValue)
                {
                    var (us, ue) = PhtDayToUtcRange(parsedDate.Value);
                    rfidQ = rfidQ.Where(l => l.TapInTime >= us && l.TapInTime < ue);
                }
                var rfidAll = await rfidQ.OrderByDescending(l => l.TapInTime).Take(100).ToListAsync();

                // Computer sessions
                var compQ = _context.ComputerSessions.Include(s => s.User).Include(s => s.ComputerUnit).AsQueryable();
                if (parsedDate.HasValue)
                    compQ = compQ.Where(s => s.StartTime.Date == parsedDate.Value.Date);
                ViewBag.ComputerSessions = await compQ.OrderByDescending(s => s.StartTime).Take(100).ToListAsync();

                // Book reservations
                var bookQ = _context.BookReservations.Include(r => r.User).Include(r => r.Book).AsQueryable();
                if (parsedDate.HasValue)
                    bookQ = bookQ.Where(r => r.CreatedAt.Date == parsedDate.Value.Date);
                ViewBag.BookLogs = await bookQ.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();

                return View(rfidAll);
            }

            // ── Library Log (default) ─────────────────────────────────────────
            var libraryQuery = _context.RFIDLogs.Include(log => log.User).AsQueryable();
            if (parsedDate.HasValue)
            {
                var (utcStart, utcEnd) = PhtDayToUtcRange(parsedDate.Value);
                libraryQuery = libraryQuery.Where(log => log.TapInTime >= utcStart && log.TapInTime < utcEnd);
            }

            var rfidLogs = await libraryQuery
                .OrderByDescending(log => log.TapInTime)
                .Take(100)
                .ToListAsync();

            return View(rfidLogs);
        }

        // ── Export to Excel ───────────────────────────────────────────────────

        // Downloads the activity log as an Excel file.
        // Applies the same filters as the log view (date, type).
        // The header row is styled with a dark navy background and white text.
        public async Task<IActionResult> ExportActivity(string? date, string? logType)
        {
            logType ??= "All";

            // Try to parse the date filter
            DateTime? parsedDate = null;
            bool dateWasProvided = !string.IsNullOrWhiteSpace(date);
            if (dateWasProvided && DateTime.TryParse(date, out DateTime parsedResult))
                parsedDate = parsedResult;

            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("Activity Log");

            if (logType == "Computer")
            {
                // Load computer session data
                var query = _context.ComputerSessions
                    .Include(session => session.User)
                    .Include(session => session.ComputerUnit)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    query = query.Where(session => session.StartTime.Date == parsedDate.Value.Date);

                var rows = await query.OrderByDescending(session => session.StartTime).ToListAsync();

                // Write header row
                sheet.Cells[1, 1].Value = "ID Number";
                sheet.Cells[1, 2].Value = "Name";
                sheet.Cells[1, 3].Value = "Computer Unit";
                sheet.Cells[1, 4].Value = "Start";
                sheet.Cells[1, 5].Value = "End";
                sheet.Cells[1, 6].Value = "Status";

                // Write each session row
                for (int i = 0; i < rows.Count; i++)
                {
                    var session  = rows[i];
                    int rowIndex = i + 2; // Row 1 is the header, data starts at row 2

                    sheet.Cells[rowIndex, 1].Value = session.User?.StudentNumber ?? session.User?.EmployeeNumber;
                    sheet.Cells[rowIndex, 2].Value = $"{session.User?.LastName}, {session.User?.FirstName}";
                    sheet.Cells[rowIndex, 3].Value = session.ComputerUnit?.ComputerNumber;
                    sheet.Cells[rowIndex, 4].Value = session.StartTime.ToString("MMM d, yyyy h:mm tt");
                    sheet.Cells[rowIndex, 5].Value = session.EndTime?.ToString("MMM d, yyyy h:mm tt") ?? "-";
                    sheet.Cells[rowIndex, 6].Value = session.EndTime == null ? "Active" : "Done";
                }
            }
            else if (logType == "Book")
            {
                // Load book reservation data
                var query = _context.BookReservations
                    .Include(reservation => reservation.User)
                    .Include(reservation => reservation.Book)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    query = query.Where(reservation => reservation.CreatedAt.Date == parsedDate.Value.Date);

                var rows = await query.OrderByDescending(reservation => reservation.CreatedAt).ToListAsync();

                // Write header row
                sheet.Cells[1, 1].Value = "ID Number";
                sheet.Cells[1, 2].Value = "Name";
                sheet.Cells[1, 3].Value = "Book Title";
                sheet.Cells[1, 4].Value = "Date";
                sheet.Cells[1, 5].Value = "Due Date";
                sheet.Cells[1, 6].Value = "Status";

                // Write each reservation row
                for (int i = 0; i < rows.Count; i++)
                {
                    var reservation = rows[i];
                    int rowIndex    = i + 2;

                    sheet.Cells[rowIndex, 1].Value = reservation.User?.StudentNumber ?? reservation.User?.EmployeeNumber;
                    sheet.Cells[rowIndex, 2].Value = $"{reservation.User?.LastName}, {reservation.User?.FirstName}";
                    sheet.Cells[rowIndex, 3].Value = reservation.Book?.Title;
                    sheet.Cells[rowIndex, 4].Value = reservation.CreatedAt.ToString("MMM d, yyyy");
                    sheet.Cells[rowIndex, 5].Value = reservation.DueDate?.ToString("MMM d, yyyy") ?? "-";
                    sheet.Cells[rowIndex, 6].Value = reservation.Status;
                }
            }
            else
            {
                // Load library RFID entry/exit data
                var query = _context.RFIDLogs.Include(log => log.User).AsQueryable();
                if (parsedDate.HasValue)
                {
                    var dayStartPht = parsedDate.Value.Date;
                    var utcStart    = TimeZoneInfo.ConvertTimeToUtc(dayStartPht, _phZone);
                    var utcEnd      = utcStart.AddDays(1);
                    query = query.Where(log => log.TapInTime >= utcStart && log.TapInTime < utcEnd);
                }

                var rows = await query.OrderByDescending(log => log.TapInTime).ToListAsync();

                // Write header row
                sheet.Cells[1, 1].Value = "ID Number";
                sheet.Cells[1, 2].Value = "Name";
                sheet.Cells[1, 3].Value = "User Type";
                sheet.Cells[1, 4].Value = "Section";
                sheet.Cells[1, 5].Value = "Tap In";
                sheet.Cells[1, 6].Value = "Tap Out";

                // Write each RFID log row
                for (int i = 0; i < rows.Count; i++)
                {
                    var log      = rows[i];
                    int rowIndex = i + 2;

                    sheet.Cells[rowIndex, 1].Value = log.User?.StudentNumber ?? log.User?.EmployeeNumber;
                    sheet.Cells[rowIndex, 2].Value = $"{log.User?.LastName}, {log.User?.FirstName}";
                    sheet.Cells[rowIndex, 3].Value = log.User?.UserType;
                    sheet.Cells[rowIndex, 4].Value = log.User?.Section;
                    var tapInPh  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(log.TapInTime, DateTimeKind.Utc), _phZone);
                    var tapOutPh = log.TapOutTime.HasValue
                        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(log.TapOutTime.Value, DateTimeKind.Utc), _phZone)
                        : (DateTime?)null;
                    sheet.Cells[rowIndex, 5].Value = tapInPh.ToString("MMM d, yyyy h:mm tt");
                    sheet.Cells[rowIndex, 6].Value = tapOutPh?.ToString("MMM d, yyyy h:mm tt") ?? "-";
                }
            }

            sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

            // Style the header row with BookHive's navy blue color and white text
            using var headerRange = sheet.Cells[1, 1, 1, 6];
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            headerRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(35, 53, 77));
            headerRange.Style.Font.Color.SetColor(System.Drawing.Color.White);

            string dateLabel = parsedDate.HasValue ? parsedDate.Value.ToString("yyyyMMdd") : "All";
            string fileName  = $"ActivityLog_{logType}_{dateLabel}.xlsx";

            return File(package.GetAsByteArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ── Librarian Profile ─────────────────────────────────────────────────

        // Shows the librarian's own profile page.
        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            return View(user);
        }

        // Saves changes the librarian made to their profile.
        // The profile picture is converted to a text string (base64) and stored in the database
        // so we don't need separate file storage.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(string firstName, string lastName, string middleName, string phoneNumber, IFormFile? profilePicture)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            user.FirstName   = firstName   ?? user.FirstName;
            user.LastName    = lastName    ?? user.LastName;
            user.MiddleName  = middleName  ?? user.MiddleName;
            user.PhoneNumber = phoneNumber ?? user.PhoneNumber;

            bool pictureUploaded = profilePicture != null && profilePicture.Length > 0;
            if (pictureUploaded)
            {
                using var memoryStream = new MemoryStream();
                await profilePicture!.CopyToAsync(memoryStream);

                string base64Image   = Convert.ToBase64String(memoryStream.ToArray());
                string mimeType      = profilePicture.ContentType;
                user.ProfilePicture  = $"data:{mimeType};base64,{base64Image}";
            }

            await _userManager.UpdateAsync(user);
            TempData["Success"] = "Profile updated successfully.";
            return RedirectToAction("Profile");
        }

        // ── Who's Inside the Library Right Now ────────────────────────────────

        // Returns a live list of everyone currently inside the library (based on RFID tap-in records).
        // The tap-in time is converted to Philippine time (Asia/Manila) for display.
        // Called by the dashboard page to update the active users table.
        public async Task<IActionResult> GetActiveUsers()
        {
            var logs = await _context.RFIDLogs
                .Include(log => log.User)
                .Where(log => log.IsInside)
                .OrderBy(log => log.TapInTime)
                .ToListAsync();

            TimeZoneInfo phZone = _phZone;

            var result = logs.Select(log =>
            {
                // Convert the stored UTC time to Philippine time for display
                DateTime tapInPhTime = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(log.TapInTime, DateTimeKind.Utc), phZone);

                return new
                {
                    name     = log.User != null ? log.User.LastName + ", " + log.User.FirstName : "Unknown",
                    userType = log.User?.UserType ?? "",
                    section  = log.User?.Section ?? "",
                    tapIn    = tapInPhTime.ToString("hh:mm tt")
                };
            });

            return Json(result);
        }

        // ── Section Management AJAX Helpers ───────────────────────────────────

        // Returns the list of students currently assigned to a specific section.
        // Used by the section management panel when the librarian selects a section.
        public async Task<IActionResult> GetStudentsBySection(string sectionName)
        {
            var students = await _context.Users
                .Where(user => user.Section == sectionName && user.UserType == "Student")
                .OrderBy(user => user.LastName).ThenBy(user => user.FirstName)
                .ToListAsync();

            var result = students.Select(student =>
            {
                // Format middle initial (e.g., "Santos" → "S.")
                string middleInitial = !string.IsNullOrEmpty(student.MiddleName)
                    ? " " + student.MiddleName.Substring(0, 1) + "."
                    : "";

                return new
                {
                    id     = student.Id,
                    name   = student.LastName + ", " + student.FirstName + middleInitial,
                    idNo   = student.StudentNumber,
                    email  = student.Email,
                    active = student.IsActive
                };
            });

            return Json(result);
        }

        // Returns all students who haven't been assigned to a section yet.
        // These are students who registered but are still waiting to be placed by the librarian.
        public async Task<IActionResult> GetUnassignedStudents()
        {
            var students = await _context.Users
                .Where(user => user.UserType == "Student"
                    && (user.Section == null || user.Section == ""))
                .OrderBy(user => user.LastName).ThenBy(user => user.FirstName)
                .ToListAsync();

            var result = students.Select(student =>
            {
                string middleInitial = !string.IsNullOrEmpty(student.MiddleName)
                    ? " " + student.MiddleName.Substring(0, 1) + "."
                    : "";

                return new
                {
                    id   = student.Id,
                    name = student.LastName + ", " + student.FirstName + middleInitial,
                    idNo = student.StudentNumber,
                    rfid = student.RFIDNumber
                };
            });

            return Json(result);
        }

        // Assigns a student to a section and activates their account.
        // Also copies the Level and Course from the section record so those fields show
        // correctly in the user detail and MIS pages.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignStudentToSection(string userId, string sectionName)
        {
            var student = await _userManager.FindByIdAsync(userId);
            if (student == null)
                return Json(new { success = false, message = "Student not found." });

            student.Section  = sectionName;
            student.IsActive = true;

            // Copy Level and Course from the section so the user's profile shows the right values
            var section = await _context.Sections.FirstOrDefaultAsync(s => s.SectionName == sectionName);
            if (section != null)
            {
                student.Level  = section.Level;
                student.Course = section.Course;
            }

            await _userManager.UpdateAsync(student);
            return Json(new { success = true });
        }

        // Marks a student as irregular instead of assigning them to a fixed section.
        // Irregular students don't belong to a specific class but can still use the library.
        // The librarian provides the student's course and the Program Head's name as their adviser.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignIrregularStudent(string userId, string course, string programHead)
        {
            var student = await _userManager.FindByIdAsync(userId);
            if (student == null)
                return Json(new { success = false, message = "Student not found." });

            student.IsIrregular  = true;
            student.Section      = "Irregular";   // So the system knows they are activated
            student.Course       = course;
            student.Level        = "Irregular";
            student.AdviserName  = programHead;   // Program Head acts as their adviser for reminders
            student.IsActive     = true;

            await _userManager.UpdateAsync(student);
            return Json(new { success = true });
        }

        // Removes a student from their section and deactivates their account.
        // The student will be logged out automatically on their next action.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveStudentFromSection(string userId)
        {
            var student = await _userManager.FindByIdAsync(userId);
            if (student == null)
                return Json(new { success = false });

            student.Section  = "";
            student.IsActive = false;
            await _userManager.UpdateAsync(student);
            return Json(new { success = true });
        }

        // ── Notification Bell ─────────────────────────────────────────────────

        // Returns the latest pending book reservations for the notification bell.
        // The librarian layout header calls this every few seconds to keep the count updated.
        public async Task<IActionResult> GetNotifications()
        {
            var pendingReservations = await _context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation => reservation.Status == "Pending")
                .OrderByDescending(reservation => reservation.CreatedAt)
                .Take(20)
                .ToListAsync();

            var notificationItems = pendingReservations.Select(reservation => new
            {
                reservation.Id,
                user      = reservation.User != null ? reservation.User.FirstName + " " + reservation.User.LastName : "Unknown",
                userType  = reservation.User?.UserType ?? "",
                book      = reservation.Book?.Title ?? "Unknown",
                createdAt = reservation.CreatedAt.ToString("MMM d, h:mm tt")
            });

            return Json(new { count = pendingReservations.Count, items = notificationItems });
        }

        // ── RFID: Librarian Desk Reader ───────────────────────────────────────

        // Called by the RFID reader on the librarian's desk (the small reader near the borrow desk).
        // Accepts GET or POST, and both "rfidNumber" and "rfid" parameter names because
        // different versions of the ESP32 firmware use different names.
        //
        // What happens when a card is tapped:
        //   1. Sends the card number to the "transaction" group → the borrow desk page auto-fills the form
        //   2. Sends to the "mis" group → the MIS registration page can auto-fill the RFID field
        //   3. Returns the user's name (or empty if the card is not registered)
        [HttpGet, HttpPost]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> DeskTap(string? rfidNumber, string? rfid)
        {
            // Only accept taps from a real BookHive RFID reader — same device key
            // check used by the door reader. Without this, anyone who finds this
            // URL could query it anonymously or inject fake taps into a librarian's
            // open Borrow/Sectioning/MIS page.
            if (!IsValidDeviceKey()) return Unauthorized();

            // Accept either "rfidNumber" or "rfid" as the parameter name
            string cardId = (rfidNumber ?? rfid ?? "").Trim();
            if (string.IsNullOrEmpty(cardId))
                return Ok(new { ok = false, error = "No rfidNumber received" });

            // Broadcast to the borrow desk and MIS registration pages
            await _hub.Clients.Group("transaction").SendAsync("DeskRfidTap",       new { rfidNumber = cardId });
            await _hub.Clients.Group("mis").SendAsync("UnregisteredRfidTap",       new { rfidNumber = cardId });

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == cardId);

            bool cardIsRegistered = user != null;
            return cardIsRegistered
                ? Ok(new { ok = true, name = $"{user!.FirstName} {user.LastName}" })
                : Ok(new { ok = true, name = "" }); // Card not found — the browser shows a "not found" message
        }

        // ── RFID: Library Entrance Reader ─────────────────────────────────────

        // Called by the RFID reader at the library entrance (mounted at the door).
        // First checks the device key to make sure it's a real authorized reader.
        //
        // How it works:
        //   - Student taps their card once → they're recorded as having entered (Tap In)
        //   - Student taps again when leaving → they're recorded as having exited (Tap Out)
        //
        // Special case: if a student has no section yet, shows an "incomplete" message
        // on the kiosk screen instead of a normal welcome.
        //
        // Sends live updates to:
        //   - The kiosk screen (the welcome display at the entrance)
        //   - All librarian dashboard screens
        [HttpPost]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> RfidTap(string rfidNumber)
        {
            if (!IsValidDeviceKey()) return Unauthorized();

            // Always notify MIS so they can use the RFID card number in the registration form
            await _hub.Clients.Group("mis").SendAsync("UnregisteredRfidTap", new { rfidNumber });

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfidNumber);
            if (user == null)   return NotFound("RFID not registered.");
            if (!user.IsActive) return Unauthorized("This account has been deactivated.");

            // Student has no section — their account isn't fully set up yet
            bool accountIsIncomplete = user.UserType == "Student" && string.IsNullOrEmpty(user.Section);
            if (accountIsIncomplete)
            {
                string firstInitial = user.FirstName?.Length > 0 ? user.FirstName[0].ToString().ToUpper() : "?";
                var incompletePayload = new
                {
                    action        = "incomplete",
                    name          = $"{user.FirstName} {user.LastName}",
                    studentNumber = user.StudentNumber ?? "",
                    userType      = user.UserType,
                    section       = "",
                    profilePic    = user.ProfilePicture ?? "",
                    initial       = firstInitial,
                    time          = PhTime().ToString("hh:mm tt")
                };
                await _hub.Clients.Group("kiosk").SendAsync("KioskTap", incompletePayload);
                return Ok(new { action = "incomplete", name = $"{user.FirstName} {user.LastName}" });
            }

            // Check if this person already has an open entry (they're currently inside)
            var openLog = await _context.RFIDLogs
                .Where(log => log.UserId == user.Id && log.IsInside)
                .OrderByDescending(log => log.TapInTime)
                .FirstOrDefaultAsync();

            string action;
            bool userIsInsideAlready = openLog != null;

            if (userIsInsideAlready)
            {
                // They were already inside → this tap means they're leaving
                openLog!.IsInside    = false;
                openLog.TapOutTime  = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                action = "TapOut";
            }
            else
            {
                // They weren't inside → this tap means they're entering
                var newEntry = new RFIDLog { UserId = user.Id, TapInTime = DateTime.UtcNow, IsInside = true };
                _context.RFIDLogs.Add(newEntry);
                await _context.SaveChangesAsync();
                action = "TapIn";
            }

            // Notify all librarian dashboard screens
            var dashboardPayload = new
            {
                action,
                name     = $"{user.FirstName} {user.LastName}",
                userType = user.UserType,
                time     = PhTime().ToString("MMM d, h:mm tt")
            };
            var allLibrarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var librarian in allLibrarians)
                await _hub.Clients.Group($"user-{librarian.Id}").SendAsync("LibraryEntryUpdated", dashboardPayload);
            await _hub.Clients.Group("librarians").SendAsync("LibraryEntryUpdated", dashboardPayload);

            // Notify the entrance kiosk screen (the welcome/goodbye display)
            string userInitial = user.FirstName?.Length > 0 ? user.FirstName[0].ToString().ToUpper() : "?";
            string tapLabel    = action == "TapIn" ? "TAP IN" : "TAP OUT";

            var kioskPayload = new
            {
                action,
                name          = $"{user.FirstName} {user.LastName}",
                studentNumber = user.StudentNumber ?? user.EmployeeNumber ?? "",
                userType      = user.UserType,
                section       = user.Section ?? "",
                profilePic    = user.ProfilePicture ?? "",
                initial       = userInitial,
                time          = PhTime().ToString("hh:mm tt"),
                tapLabel
            };
            await _hub.Clients.Group("kiosk").SendAsync("KioskTap", kioskPayload);

            return Ok(new { action, name = $"{user.FirstName} {user.LastName}", userType = user.UserType });
        }

        // ── Philippine Time Helper ────────────────────────────────────────────

        // Gets the Philippine timezone. On Linux servers it's "Asia/Manila", on Windows it's
        // "Singapore Standard Time" (same timezone, just different name).
        // Falls back to UTC if neither is found.
        private static readonly TimeZoneInfo _phZone =
            TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(zone =>
                zone.Id == "Asia/Manila" || zone.Id == "Singapore Standard Time")
            ?? TimeZoneInfo.Utc;

        // Returns the current time in the Philippines.
        private static DateTime PhTime() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _phZone);
    }
}
