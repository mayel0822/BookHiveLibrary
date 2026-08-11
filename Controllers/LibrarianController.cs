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
    [Authorize(Roles = "LIBRARIAN")]
    public class LibrarianController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub;
        private readonly IConfiguration _config;

        public LibrarianController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<LibraryHub> hub, IConfiguration config)
        {
            _context = context;
            _userManager = userManager;
            _hub = hub;
            _config = config;
        }

        private bool IsValidDeviceKey() => Request.Headers["X-Device-Key"] == _config["RfidDeviceKey"];

        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public IActionResult Kiosk() => View();

        public async Task<IActionResult> Dashboard()
        {
            ViewBag.ActiveUsers = await _context.RFIDLogs.CountAsync(l => l.IsInside);
            ViewBag.BookBorrowed = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp");
            ViewBag.Reservations = await _context.BookReservations.CountAsync(r => r.Status == "Pending");
            ViewBag.ComputerInUse = await _context.ComputerSessions.CountAsync(s => s.EndTime == null);
            ViewBag.ComputerAvailable = await _context.ComputerUnits.CountAsync(c => !c.IsArchived && c.IsAvailable);

            ViewBag.CurrentBorrowers = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" || r.Status == "Overdue")
                .OrderBy(r => r.DueDate)
                .Take(10)
                .ToListAsync();

            ViewBag.ReservationList = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "Pending")
                .OrderBy(r => r.CreatedAt)
                .Take(10)
                .ToListAsync();

            ViewBag.BookDues = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "PickedUp" && r.DueDate <= DateTime.Now.AddDays(1))
                .OrderBy(r => r.DueDate)
                .Take(10)
                .ToListAsync();

            return View();
        }

        // ── User Management ──────────────────────────────────────────────────

        public async Task<IActionResult> UserManagement(string? search, string? userType)
        {
            var query = _userManager.Users
                .Where(u => u.IsActive && (u.UserType == "Student" || u.UserType == "Professor"));

            if (!string.IsNullOrWhiteSpace(userType))
                query = query.Where(u => u.UserType == userType);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u =>
                    u.FirstName.Contains(search) ||
                    u.LastName.Contains(search) ||
                    u.StudentNumber.Contains(search) ||
                    u.EmployeeNumber.Contains(search) ||
                    u.Email!.Contains(search));

            var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();

            ViewBag.Search = search;
            ViewBag.UserType = userType;
            ViewBag.Sections = await _context.Sections.OrderBy(s => s.SectionName).ToListAsync();

            return View(users);
        }

        // ── Sectioning ───────────────────────────────────────────────────────

        public async Task<IActionResult> Sectioning()
        {
            ViewBag.Sections = await _context.Sections.OrderBy(s => s.Level).ThenBy(s => s.SectionName).ToListAsync();
            ViewBag.Professors = await _context.Users
                .Where(u => u.UserType == "Professor" && u.IsActive)
                .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
                .Select(u => u.LastName + ", " + u.FirstName)
                .ToListAsync();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSection(string level, string course, string year, string sectionName, string adviserName)
        {
            _context.Sections.Add(new Section
            {
                Level = level,
                Course = course,
                Year = year,
                SectionName = sectionName,
                AdviserName = adviserName ?? ""
            });
            await _context.SaveChangesAsync();
            TempData["Success"] = "Section created.";
            return RedirectToAction("Sectioning");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllSections(string levelGroup)
        {
            IQueryable<Section> query = _context.Sections;

            if (levelGroup == "JHS")
                query = query.Where(s => s.Level == "Junior High School");
            else if (levelGroup == "SHS")
                query = query.Where(s => s.Level == "Senior High School");
            else if (levelGroup == "Tertiary")
                query = query.Where(s => s.Level == "Tertiary");

            var sectionsToDelete = await query.ToListAsync();
            var sectionNames     = sectionsToDelete.Select(s => s.SectionName).ToList();

            var affected = await _context.Users
                .Where(u => u.UserType == "Student" && u.Section != null && sectionNames.Contains(u.Section))
                .ToListAsync();
            foreach (var u in affected) { u.Section = ""; u.IsActive = false; }

            _context.Sections.RemoveRange(sectionsToDelete);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"{levelGroup} sections have been reset.";
            return RedirectToAction("Sectioning");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSection(int id)
        {
            var section = await _context.Sections.FindAsync(id);
            if (section != null)
            {
                var affected = await _context.Users
                    .Where(u => u.UserType == "Student" && u.Section == section.SectionName)
                    .ToListAsync();
                foreach (var u in affected) { u.Section = ""; u.IsActive = false; }

                _context.Sections.Remove(section);
                await _context.SaveChangesAsync();
            }
            TempData["Success"] = "Section deleted.";
            return RedirectToAction("Sectioning");
        }

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetAllSections(string userType)
        {
            var users = _userManager.Users.Where(u => u.UserType == userType).ToList();
            foreach (var u in users)
                u.Section = "";
            foreach (var u in users)
                await _userManager.UpdateAsync(u);
            TempData["Success"] = $"All {userType} sections have been reset.";
            return RedirectToAction("UserManagement");
        }

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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActivateWithSection(string userId, string section)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.Section = section;
                user.IsActive = true;
                await _userManager.UpdateAsync(user);
            }
            return RedirectToAction("UserManagement");
        }

        // ── Student Activity (RFID live tracker) ─────────────────────────────

        public async Task<IActionResult> StudentActivity(string? date, string? logType)
        {
            logType ??= "Library";

            DateTime? parsedDate = null;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var d))
                parsedDate = d;

            if (logType == "Computer")
            {
                var csQuery = _context.ComputerSessions
                    .Include(s => s.User)
                    .Include(s => s.ComputerUnit)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    csQuery = csQuery.Where(s => s.StartTime.Date == parsedDate.Value.Date);

                ViewBag.ComputerSessions = await csQuery.OrderByDescending(s => s.StartTime).Take(100).ToListAsync();
                ViewBag.Date = date;
                ViewBag.LogType = logType;
                return View(new List<RFIDLog>());
            }

            if (logType == "Book")
            {
                var bQuery = _context.BookReservations
                    .Include(r => r.User)
                    .Include(r => r.Book)
                    .AsQueryable();

                if (parsedDate.HasValue)
                    bQuery = bQuery.Where(r => r.CreatedAt.Date == parsedDate.Value.Date);

                ViewBag.BookLogs = await bQuery.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
                ViewBag.Date = date;
                ViewBag.LogType = logType;
                return View(new List<RFIDLog>());
            }

            var rfidQuery = _context.RFIDLogs
                .Include(l => l.User)
                .AsQueryable();

            if (parsedDate.HasValue)
                rfidQuery = rfidQuery.Where(l => l.TapInTime.Date == parsedDate.Value.Date);

            var logs = await rfidQuery.OrderByDescending(l => l.TapInTime).Take(100).ToListAsync();

            ViewBag.Date = date;
            ViewBag.LogType = logType;

            return View(logs);
        }

        // ── Export Activity Log ──────────────────────────────────────────────

        public async Task<IActionResult> ExportActivity(string? date, string? logType)
        {
            logType ??= "Library";
            DateTime? parsedDate = null;
            if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var d))
                parsedDate = d;

            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Activity Log");

            if (logType == "Computer")
            {
                var query = _context.ComputerSessions.Include(s => s.User).Include(s => s.ComputerUnit).AsQueryable();
                if (parsedDate.HasValue) query = query.Where(s => s.StartTime.Date == parsedDate.Value.Date);
                var rows = await query.OrderByDescending(s => s.StartTime).ToListAsync();

                ws.Cells[1, 1].Value = "ID Number"; ws.Cells[1, 2].Value = "Name";
                ws.Cells[1, 3].Value = "Computer Unit"; ws.Cells[1, 4].Value = "Start"; ws.Cells[1, 5].Value = "End"; ws.Cells[1, 6].Value = "Status";
                for (int i = 0; i < rows.Count; i++)
                {
                    var s = rows[i]; int r = i + 2;
                    ws.Cells[r, 1].Value = s.User?.StudentNumber ?? s.User?.EmployeeNumber;
                    ws.Cells[r, 2].Value = $"{s.User?.LastName}, {s.User?.FirstName}";
                    ws.Cells[r, 3].Value = s.ComputerUnit?.ComputerNumber;
                    ws.Cells[r, 4].Value = s.StartTime.ToString("MMM d, yyyy h:mm tt");
                    ws.Cells[r, 5].Value = s.EndTime?.ToString("MMM d, yyyy h:mm tt") ?? "-";
                    ws.Cells[r, 6].Value = s.EndTime == null ? "Active" : "Done";
                }
            }
            else if (logType == "Book")
            {
                var query = _context.BookReservations.Include(r => r.User).Include(r => r.Book).AsQueryable();
                if (parsedDate.HasValue) query = query.Where(r => r.CreatedAt.Date == parsedDate.Value.Date);
                var rows = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();

                ws.Cells[1, 1].Value = "ID Number"; ws.Cells[1, 2].Value = "Name";
                ws.Cells[1, 3].Value = "Book Title"; ws.Cells[1, 4].Value = "Date"; ws.Cells[1, 5].Value = "Due Date"; ws.Cells[1, 6].Value = "Status";
                for (int i = 0; i < rows.Count; i++)
                {
                    var b = rows[i]; int r = i + 2;
                    ws.Cells[r, 1].Value = b.User?.StudentNumber ?? b.User?.EmployeeNumber;
                    ws.Cells[r, 2].Value = $"{b.User?.LastName}, {b.User?.FirstName}";
                    ws.Cells[r, 3].Value = b.Book?.Title;
                    ws.Cells[r, 4].Value = b.CreatedAt.ToString("MMM d, yyyy");
                    ws.Cells[r, 5].Value = b.DueDate?.ToString("MMM d, yyyy") ?? "-";
                    ws.Cells[r, 6].Value = b.Status;
                }
            }
            else
            {
                var query = _context.RFIDLogs.Include(l => l.User).AsQueryable();
                if (parsedDate.HasValue) query = query.Where(l => l.TapInTime.Date == parsedDate.Value.Date);
                var rows = await query.OrderByDescending(l => l.TapInTime).ToListAsync();

                ws.Cells[1, 1].Value = "ID Number"; ws.Cells[1, 2].Value = "Name";
                ws.Cells[1, 3].Value = "User Type"; ws.Cells[1, 4].Value = "Section"; ws.Cells[1, 5].Value = "Tap In"; ws.Cells[1, 6].Value = "Tap Out";
                for (int i = 0; i < rows.Count; i++)
                {
                    var l = rows[i]; int r = i + 2;
                    ws.Cells[r, 1].Value = l.User?.StudentNumber ?? l.User?.EmployeeNumber;
                    ws.Cells[r, 2].Value = $"{l.User?.LastName}, {l.User?.FirstName}";
                    ws.Cells[r, 3].Value = l.User?.UserType;
                    ws.Cells[r, 4].Value = l.User?.Section;
                    ws.Cells[r, 5].Value = l.TapInTime.ToString("MMM d, yyyy h:mm tt");
                    ws.Cells[r, 6].Value = l.TapOutTime?.ToString("MMM d, yyyy h:mm tt") ?? "-";
                }
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();
            using var header = ws.Cells[1, 1, 1, 6];
            header.Style.Font.Bold = true;
            header.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            header.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(35, 53, 77));
            header.Style.Font.Color.SetColor(System.Drawing.Color.White);

            var fileName = $"ActivityLog_{logType}_{(parsedDate.HasValue ? parsedDate.Value.ToString("yyyyMMdd") : "All")}.xlsx";
            return File(package.GetAsByteArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        // ── My Profile ───────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Profile(string firstName, string lastName, string middleName, string phoneNumber, IFormFile? profilePicture)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

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

        // ── Active Users (currently inside library) ────────────────────────────

        public async Task<IActionResult> GetActiveUsers()
        {
            var logs = await _context.RFIDLogs
                .Include(l => l.User)
                .Where(l => l.IsInside)
                .OrderBy(l => l.TapInTime)
                .ToListAsync();

            var phZone = _phZone;
            var result = logs.Select(l => new {
                name     = l.User != null ? l.User.LastName + ", " + l.User.FirstName : "Unknown",
                userType = l.User != null ? l.User.UserType : "",
                section  = l.User != null ? l.User.Section : "",
                tapIn    = TimeZoneInfo.ConvertTimeFromUtc(
                               DateTime.SpecifyKind(l.TapInTime, DateTimeKind.Utc), phZone)
                           .ToString("hh:mm tt")
            });

            return Json(result);
        }

        public async Task<IActionResult> GetStudentsBySection(string sectionName)
        {
            var students = await _context.Users
                .Where(u => u.Section == sectionName && u.UserType == "Student")
                .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
                .Select(u => new {
                    id     = u.Id,
                    name   = u.LastName + ", " + u.FirstName + (!string.IsNullOrEmpty(u.MiddleName) ? " " + u.MiddleName.Substring(0, 1) + "." : ""),
                    idNo   = u.StudentNumber,
                    email  = u.Email,
                    active = u.IsActive
                })
                .ToListAsync();
            return Json(students);
        }

        public async Task<IActionResult> GetUnassignedStudents()
        {
            var students = await _context.Users
                .Where(u => u.UserType == "Student" && (u.Section == null || u.Section == ""))
                .OrderBy(u => u.LastName).ThenBy(u => u.FirstName)
                .Select(u => new {
                    id   = u.Id,
                    name = u.LastName + ", " + u.FirstName + (!string.IsNullOrEmpty(u.MiddleName) ? " " + u.MiddleName.Substring(0, 1) + "." : ""),
                    idNo = u.StudentNumber,
                    rfid = u.RFIDNumber
                })
                .ToListAsync();
            return Json(students);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignStudentToSection(string userId, string sectionName)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return Json(new { success = false, message = "Student not found." });

            user.Section  = sectionName;
            user.IsActive = true;

            // Copy Level and full Course name from the section record so they show in User Information
            var section = await _context.Sections.FirstOrDefaultAsync(s => s.SectionName == sectionName);
            if (section != null)
            {
                user.Level  = section.Level;
                user.Course = section.Course; // Sectioning page now stores full name; old records have abbr
            }

            await _userManager.UpdateAsync(user);

            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveStudentFromSection(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return Json(new { success = false });

            user.Section  = "";
            user.IsActive = false;
            await _userManager.UpdateAsync(user);

            return Json(new { success = true });
        }

        // ── Notifications ─────────────────────────────────────────────────────

        public async Task<IActionResult> GetNotifications()
        {
            var pending = await _context.BookReservations
                .Include(r => r.User)
                .Include(r => r.Book)
                .Where(r => r.Status == "Pending")
                .OrderByDescending(r => r.CreatedAt)
                .Take(20)
                .Select(r => new {
                    r.Id,
                    user = r.User != null ? r.User.FirstName + " " + r.User.LastName : "Unknown",
                    userType = r.User != null ? r.User.UserType : "",
                    book = r.Book != null ? r.Book.Title : "Unknown",
                    createdAt = r.CreatedAt.ToString("MMM d, h:mm tt")
                })
                .ToListAsync();

            return Json(new { count = pending.Count, items = pending });
        }

        // RFID tap-in (called by IoT device via HTTP)
        [HttpPost]
        public async Task<IActionResult> RfidTapIn(string rfidNumber)
        {
            if (!IsValidDeviceKey()) return Unauthorized();

            // Always notify MIS registration page so it can auto-fill the RFID field.
            await _hub.Clients.Group("mis").SendAsync("UnregisteredRfidTap", new { rfidNumber });

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfidNumber);
            if (user == null) return NotFound("RFID not registered.");
            if (!user.IsActive) return Unauthorized("This account has been deactivated.");

            var open = await _context.RFIDLogs
                .Where(l => l.UserId == user.Id && l.IsInside)
                .ToListAsync();
            foreach (var log in open)
            {
                log.IsInside = false;
                log.TapOutTime = DateTime.Now;
            }

            _context.RFIDLogs.Add(new RFIDLog { UserId = user.Id, TapInTime = DateTime.Now });
            await _context.SaveChangesAsync();

            var tapPayload = new { action = "TapIn", name = $"{user.FirstName} {user.LastName}", userType = user.UserType, time = PhTime().ToString("MMM d, h:mm tt") };
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var lib in librarians)
                await _hub.Clients.Group($"user-{lib.Id}").SendAsync("LibraryEntryUpdated", tapPayload);

            return Ok(new { name = $"{user.FirstName} {user.LastName}", studentNumber = user.StudentNumber });
        }

        // RFID desk reader — called by librarian desk ESP32, pushes RFID to transaction page
        // Accepts GET or POST, and rfidNumber OR rfid parameter (covers all ESP32 firmware variants)
        [HttpGet, HttpPost]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> DeskTap(string? rfidNumber, string? rfid)
        {
            var cardId = (rfidNumber ?? rfid ?? "").Trim();
            if (string.IsNullOrEmpty(cardId))
                return Ok(new { ok = false, error = "No rfidNumber received" });

            // Broadcast RFID to the transaction group (borrow desk) and MIS group (registration page).
            await _hub.Clients.Group("transaction").SendAsync("DeskRfidTap", new { rfidNumber = cardId });
            await _hub.Clients.Group("mis").SendAsync("UnregisteredRfidTap", new { rfidNumber = cardId });

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == cardId);
            return user != null
                ? Ok(new { ok = true, name = $"{user.FirstName} {user.LastName}" })
                : Ok(new { ok = true, name = "" });   // unknown card — let browser handle "not found"
        }

        // RFID single-tap toggle (tap once = enter, tap again = exit)
        [HttpPost]
        public async Task<IActionResult> RfidTap(string rfidNumber)
        {
            if (!IsValidDeviceKey()) return Unauthorized();

            // Always broadcast to MIS group so Registration page can auto-fill the RFID field
            await _hub.Clients.Group("mis").SendAsync("UnregisteredRfidTap", new { rfidNumber });

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfidNumber);
            if (user == null)
            {
                return NotFound("RFID not registered.");
            }
            if (!user.IsActive) return Unauthorized("This account has been deactivated.");

            // Student has no section yet — account is incomplete, direct to librarian
            if (user.UserType == "Student" && string.IsNullOrEmpty(user.Section))
            {
                var incompletePayload = new {
                    action        = "incomplete",
                    name          = $"{user.FirstName} {user.LastName}",
                    studentNumber = user.StudentNumber ?? "",
                    userType      = user.UserType,
                    section       = "",
                    profilePic    = user.ProfilePicture ?? "",
                    initial       = user.FirstName?.Length > 0 ? user.FirstName[0].ToString().ToUpper() : "?",
                    time          = PhTime().ToString("hh:mm tt")
                };
                await _hub.Clients.Group("kiosk").SendAsync("KioskTap", incompletePayload);
                return Ok(new { action = "incomplete", name = $"{user.FirstName} {user.LastName}" });
            }

            var openLog = await _context.RFIDLogs
                .Where(l => l.UserId == user.Id && l.IsInside)
                .OrderByDescending(l => l.TapInTime)
                .FirstOrDefaultAsync();

            string action;
            if (openLog != null)
            {
                // Currently inside → tap OUT
                openLog.IsInside = false;
                openLog.TapOutTime = DateTime.Now;
                await _context.SaveChangesAsync();
                action = "TapOut";
            }
            else
            {
                // Not inside → tap IN
                _context.RFIDLogs.Add(new RFIDLog { UserId = user.Id, TapInTime = DateTime.Now, IsInside = true });
                await _context.SaveChangesAsync();
                action = "TapIn";
            }

            var payload = new { action, name = $"{user.FirstName} {user.LastName}", userType = user.UserType, time = PhTime().ToString("MMM d, h:mm tt") };
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var lib in librarians)
                await _hub.Clients.Group($"user-{lib.Id}").SendAsync("LibraryEntryUpdated", payload);
            await _hub.Clients.Group("librarians").SendAsync("LibraryEntryUpdated", payload);

            // Broadcast to kiosk screens
            var kioskPayload = new {
                action,
                name        = $"{user.FirstName} {user.LastName}",
                studentNumber = user.StudentNumber ?? user.EmployeeNumber ?? "",
                userType    = user.UserType,
                section     = user.Section ?? "",
                profilePic  = user.ProfilePicture ?? "",
                initial     = user.FirstName?.Length > 0 ? user.FirstName[0].ToString().ToUpper() : "?",
                time        = PhTime().ToString("hh:mm tt")
            };
            await _hub.Clients.Group("kiosk").SendAsync("KioskTap", kioskPayload);

            return Ok(new { action, name = $"{user.FirstName} {user.LastName}", userType = user.UserType });
        }

        // RFID tap-out (called by IoT device via HTTP)
        [HttpPost]
        public async Task<IActionResult> RfidTapOut(string rfidNumber)
        {
            if (!IsValidDeviceKey()) return Unauthorized();

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.RFIDNumber == rfidNumber);
            if (user == null) return NotFound("RFID not registered.");

            var log = await _context.RFIDLogs
                .Where(l => l.UserId == user.Id && l.IsInside)
                .OrderByDescending(l => l.TapInTime)
                .FirstOrDefaultAsync();

            if (log != null)
            {
                log.IsInside = false;
                log.TapOutTime = DateTime.Now;
                await _context.SaveChangesAsync();
            }

            var tapOutPayload = new { action = "TapOut", name = $"{user.FirstName} {user.LastName}", userType = user.UserType, time = PhTime().ToString("MMM d, h:mm tt") };
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var lib in librarians)
                await _hub.Clients.Group($"user-{lib.Id}").SendAsync("LibraryEntryUpdated", tapOutPayload);

            return Ok();
        }

        private static readonly TimeZoneInfo _phZone =
            TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(z =>
                z.Id == "Asia/Manila" || z.Id == "Singapore Standard Time")
            ?? TimeZoneInfo.Utc;

        private static DateTime PhTime() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _phZone);
    }
}
