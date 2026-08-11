using BookHiveLibrary.Data;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Controllers
{
    [Authorize(Roles = "LIBRARIAN")]
    public class ComputerController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub;

        public ComputerController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<LibraryHub> hub)
        {
            _context = context;
            _userManager = userManager;
            _hub = hub;
        }

        private async Task PushComputerEvent(string eventName, object payload)
        {
            var librarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var lib in librarians)
                await _hub.Clients.Group($"user-{lib.Id}").SendAsync(eventName, payload);
        }

        private async Task LoadComputerStats()
        {
            ViewBag.TotalComputers = await _context.ComputerUnits.CountAsync();
            ViewBag.AvailableComputers = await _context.ComputerUnits.CountAsync(c => !c.IsArchived && c.IsAvailable);
            ViewBag.InUseComputers = await _context.ComputerUnits.CountAsync(c => !c.IsArchived && !c.IsAvailable);
            ViewBag.ArchivedComputers = await _context.ComputerUnits.CountAsync(c => c.IsArchived);
        }

        // Computer List
        public async Task<IActionResult> Index()
        {
            await LoadComputerStats();

            var computers = await _context.ComputerUnits
                .Where(c => !c.IsArchived)
                .Include(c => c.Sessions.Where(s => s.IsActive))
                    .ThenInclude(s => s.User)
                .OrderBy(c => c.ComputerNumber)
                .ToListAsync();

            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(s => s.SectionName, s => s.AdviserName);

            ViewBag.AllUsers = await _userManager.Users
                .Where(u => (u.UserType == "Student" || u.UserType == "Professor") && u.IsActive)
                .OrderBy(u => u.LastName)
                .ToListAsync();

            return View(computers);
        }

        // Transaction Management - Computer
        public async Task<IActionResult> Transaction()
        {
            var computers = await _context.ComputerUnits
                .Where(c => !c.IsArchived)
                .Include(c => c.Sessions.Where(s => s.IsActive))
                    .ThenInclude(s => s.User)
                .OrderBy(c => c.ComputerNumber)
                .ToListAsync();

            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(s => s.SectionName, s => s.AdviserName);

            return View(computers);
        }

        // RFID lookup for computer assignment
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
                studentNumber = user.UserType == "Student" ? user.StudentNumber : user.EmployeeNumber,
                firstName = user.FirstName,
                lastName = user.LastName,
                middleName = user.MiddleName ?? "",
                section = user.Section ?? "",
                adviserName = sectionRecord?.AdviserName ?? "",
                userType = user.UserType
            });
        }

        // Register / Add Computer
        public async Task<IActionResult> Register()
        {
            await LoadComputerStats();
            return View(new ComputerUnit());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string computerNumber, bool isAvailable = true)
        {
            if (string.IsNullOrWhiteSpace(computerNumber))
            {
                await LoadComputerStats();
                ModelState.AddModelError("", "Computer number is required.");
                return View(new ComputerUnit());
            }

            bool exists = await _context.ComputerUnits.AnyAsync(c => c.ComputerNumber == computerNumber && !c.IsArchived);
            if (exists)
            {
                await LoadComputerStats();
                ModelState.AddModelError("", "A computer with that number already exists.");
                return View(new ComputerUnit());
            }

            _context.ComputerUnits.Add(new ComputerUnit { ComputerNumber = computerNumber, IsAvailable = isAvailable });
            await _context.SaveChangesAsync();
            TempData["Success"] = $"{computerNumber} registered.";
            return RedirectToAction("Index");
        }

        // Start a session (librarian assigns student to a computer)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartSession(int computerId, string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["Error"] = "No student selected. Please tap the RFID card first.";
                return RedirectToAction("Transaction");
            }

            var computer = await _context.ComputerUnits.FindAsync(computerId);
            if (computer == null || !computer.IsAvailable)
            {
                TempData["Error"] = "Computer is not available.";
                return RedirectToAction("Transaction");
            }

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                TempData["Error"] = "Student not found.";
                return RedirectToAction("Transaction");
            }

            _context.ComputerSessions.Add(new ComputerSession
            {
                ComputerUnitId = computerId,
                UserId = userId,
                StartTime = DateTime.Now
            });

            computer.IsAvailable = false;
            await _context.SaveChangesAsync();
            await PushComputerEvent("ComputerSessionUpdated", new { action = "Started", computerId });
            TempData["Success"] = $"Session started for {user.FirstName} {user.LastName}.";
            return RedirectToAction("Transaction");
        }

        // Auto-end session via AJAX (called by librarian UI when timer expires)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndSessionAjax(int sessionId)
        {
            var session = await _context.ComputerSessions
                .Include(s => s.ComputerUnit)
                .FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null) return Json(new { success = false });

            session.EndTime = DateTime.Now;
            session.IsActive = false;
            if (session.ComputerUnit != null)
                session.ComputerUnit.IsAvailable = true;

            await _context.SaveChangesAsync();
            await PushComputerEvent("ComputerSessionUpdated", new { action = "Ended", computerId = session.ComputerUnitId });
            return Json(new { success = true, computerId = session.ComputerUnitId });
        }

        // Kiosk page — fullscreen LAN page on student computer (no login needed)
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Kiosk(string pc)
        {
            ViewBag.PcNumber = pc;
            return View();
        }

        // Kiosk status poll endpoint (no login needed — called from student kiosk PC)
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> KioskStatus(string pc)
        {
            var computer = await _context.ComputerUnits
                .Include(c => c.Sessions.Where(s => s.IsActive))
                    .ThenInclude(s => s.User)
                .FirstOrDefaultAsync(c => c.ComputerNumber == pc);

            if (computer == null) return Json(new { found = false });

            var sess = computer.Sessions.FirstOrDefault(s => s.IsActive);
            if (sess == null) return Json(new { isActive = false });

            var totalSecs = (sess.AllowedMinutes + sess.ExtendedMinutes) * 60;
            var elapsed   = (int)(DateTime.Now - sess.StartTime).TotalSeconds;
            var remaining = totalSecs - elapsed;

            return Json(new
            {
                isActive  = true,
                remaining,
                totalSecs,
                name      = sess.User != null ? sess.User.LastName + ", " + sess.User.FirstName : "",
                section   = sess.User?.Section ?? ""
            });
        }

        // Extend session time
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ExtendTime(int sessionId, int minutes)
        {
            var session = await _context.ComputerSessions.FindAsync(sessionId);
            if (session == null) return NotFound();
            session.ExtendedMinutes += minutes;
            await _context.SaveChangesAsync();
            return Json(new {
                success = true,
                totalMinutes = session.AllowedMinutes + session.ExtendedMinutes
            });
        }

        // End session
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndSession(int sessionId)
        {
            var session = await _context.ComputerSessions
                .Include(s => s.ComputerUnit)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null) return NotFound();

            session.EndTime = DateTime.Now;
            session.IsActive = false;

            if (session.ComputerUnit != null)
                session.ComputerUnit.IsAvailable = true;

            await _context.SaveChangesAsync();
            await PushComputerEvent("ComputerSessionUpdated", new { action = "Ended", computerId = session.ComputerUnitId });
            TempData["Success"] = "Session ended.";
            return RedirectToAction("Transaction");
        }

        // Archive Computer
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Archive(int id, string reason)
        {
            var computer = await _context.ComputerUnits.FindAsync(id);
            if (computer == null) return NotFound();

            computer.IsArchived = true;
            computer.IsAvailable = false;
            computer.ArchiveReason = reason;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Computer archived.";
            return RedirectToAction("Index");
        }

        // Archived computers list
        public async Task<IActionResult> ArchiveList()
        {
            await LoadComputerStats();
            var archived = await _context.ComputerUnits
                .Where(c => c.IsArchived)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
            return View(archived);
        }

        // Permanently delete archived computer
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var computer = await _context.ComputerUnits.FindAsync(id);
            if (computer != null)
            {
                _context.ComputerUnits.Remove(computer);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Computer permanently deleted.";
            }
            return RedirectToAction("ArchiveList");
        }

        // Restore from archive
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(int id)
        {
            var computer = await _context.ComputerUnits.FindAsync(id);
            if (computer == null) return NotFound();

            computer.IsArchived = false;
            computer.IsAvailable = true;
            computer.ArchiveReason = "";

            await _context.SaveChangesAsync();
            TempData["Success"] = "Computer restored.";
            return RedirectToAction("ArchiveList");
        }
    }
}
