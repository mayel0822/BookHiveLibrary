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
    // This controller handles everything related to the computer lab.
    // Only librarians can access it (except the kiosk pages which any computer can load).
    //
    // What it handles:
    //   - Seeing which computers are available or in use
    //   - Starting and ending student sessions
    //   - Extending time for a session
    //   - Adding and removing computers from the system
    //   - The kiosk page that runs on each student PC showing how much time is left
    [Authorize(Roles = "LIBRARIAN")]
    public class ComputerController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IHubContext<LibraryHub> _hub; // For sending live updates to librarian screens

        public ComputerController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IHubContext<LibraryHub> hub)
        {
            _context     = context;
            _userManager = userManager;
            _hub         = hub;
        }

        // Sends a live update about a session change to all librarian screens.
        // This way the librarian's transaction page updates automatically without refreshing.
        private async Task PushComputerEvent(string eventName, object payload)
        {
            var allLibrarians = await _userManager.GetUsersInRoleAsync("Librarian");
            foreach (var librarian in allLibrarians)
                await _hub.Clients.Group($"user-{librarian.Id}").SendAsync(eventName, payload);
        }

        // Loads the computer count numbers (total, available, in use, archived) into ViewBag
        // so the summary cards at the top of the page always have up-to-date numbers.
        private async Task LoadComputerStats()
        {
            ViewBag.TotalComputers     = await _context.ComputerUnits.CountAsync(c => !c.IsArchived);
            ViewBag.InUseComputers     = await _context.ComputerSessions.CountAsync(s => s.IsActive);
            ViewBag.UnavailableComputers = await _context.ComputerUnits.CountAsync(c => !c.IsArchived && !c.IsAvailable
                                             && !_context.ComputerSessions.Any(s => s.ComputerUnitId == c.Id && s.IsActive));
            ViewBag.AvailableComputers = await _context.ComputerUnits.CountAsync(c => !c.IsArchived && c.IsAvailable
                                             && !_context.ComputerSessions.Any(s => s.ComputerUnitId == c.Id && s.IsActive));
            ViewBag.ArchivedComputers  = await _context.ComputerUnits.CountAsync(c => c.IsArchived);
        }

        // Shows the full list of active computers with their current session (if occupied).
        // Also loads all students/professors for the manual assignment dropdown
        // in case the librarian wants to assign someone without using the RFID card.
        public async Task<IActionResult> Index()
        {
            await LoadComputerStats();

            var computers = await _context.ComputerUnits
                .Where(computer => !computer.IsArchived)
                .Include(computer => computer.Sessions.Where(session => session.IsActive)) // Only load the currently running session
                    .ThenInclude(session => session.User)
                .OrderBy(computer => computer.ComputerNumber)
                .ToListAsync();

            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(section => section.SectionName, section => section.AdviserName);

            ViewBag.AllUsers = await _userManager.Users
                .Where(user => (user.UserType == "Student" || user.UserType == "Professor") && user.IsActive)
                .OrderBy(user => user.LastName)
                .ToListAsync();

            return View(computers);
        }

        // The real-time transaction page where the librarian manages computer sessions.
        // Updates automatically via SignalR when sessions start or end.
        public async Task<IActionResult> Transaction()
        {
            await LoadComputerStats();

            var computers = await _context.ComputerUnits
                .Where(computer => !computer.IsArchived)
                .Include(computer => computer.Sessions.Where(session => session.IsActive))
                    .ThenInclude(session => session.User)
                .OrderBy(computer => computer.ComputerNumber)
                .ToListAsync();

            ViewBag.SectionAdvisers = await _context.Sections
                .ToDictionaryAsync(section => section.SectionName, section => section.AdviserName);

            return View(computers);
        }

        // Looks up a student by their RFID card number.
        // When the student taps their card on the desk reader, the librarian's screen
        // automatically fills in the student's name, section, and ID number.
        // Returns an error message if the card is not recognized or the account isn't activated.
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
                studentNumber = user.UserType == "Student" ? user.StudentNumber : user.EmployeeNumber,
                firstName     = user.FirstName,
                lastName      = user.LastName,
                middleName    = user.MiddleName ?? "",
                section       = user.Section ?? "",
                adviserName   = sectionRecord?.AdviserName ?? "",
                userType      = user.UserType
            });
        }

        // Shows the form for adding a new computer to the system.
        public async Task<IActionResult> Register()
        {
            await LoadComputerStats();
            return View(new ComputerUnit());
        }

        // Adds a new computer to the system.
        // Makes sure the computer number doesn't already exist (among active computers).
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

            bool computerAlreadyExists = await _context.ComputerUnits
                .AnyAsync(computer => computer.ComputerNumber == computerNumber && !computer.IsArchived);

            if (computerAlreadyExists)
            {
                await LoadComputerStats();
                ModelState.AddModelError("", "A computer with that number already exists.");
                return View(new ComputerUnit());
            }

            var newComputer = new ComputerUnit { ComputerNumber = computerNumber, IsAvailable = isAvailable };
            _context.ComputerUnits.Add(newComputer);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"{computerNumber} registered.";
            return RedirectToAction("Index");
        }

        // Starts a computer session — assigns a student to a specific computer.
        // The librarian clicks "Start Session" after the student taps their RFID card.
        // Checks that:
        //   - A student was actually selected (RFID was tapped)
        //   - The computer exists and is currently free
        //   - The student account exists
        // After starting: marks the computer as "not available" and notifies all librarian screens.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartSession(int computerId, string userId)
        {
            bool noStudentSelected = string.IsNullOrWhiteSpace(userId);
            if (noStudentSelected)
            {
                TempData["Error"] = "No student selected. Please tap the RFID card first.";
                return RedirectToAction("Transaction");
            }

            var computer = await _context.ComputerUnits.FindAsync(computerId);
            bool computerNotAvailable = computer == null || !computer.IsAvailable;
            if (computerNotAvailable)
            {
                TempData["Error"] = "Computer is not available.";
                return RedirectToAction("Transaction");
            }

            var student = await _userManager.FindByIdAsync(userId);
            if (student == null)
            {
                TempData["Error"] = "Student not found.";
                return RedirectToAction("Transaction");
            }

            var newSession = new ComputerSession
            {
                ComputerUnitId = computerId,
                UserId         = userId,
                StartTime      = DateTime.Now
                // The default session time (60 minutes) comes from the ComputerSession model
            };
            _context.ComputerSessions.Add(newSession);

            computer!.IsAvailable = false; // Mark the computer as occupied
            await _context.SaveChangesAsync();

            await PushComputerEvent("ComputerSessionUpdated", new { action = "Started", computerId });
            TempData["Success"] = $"Session started for {student.FirstName} {student.LastName}.";
            return RedirectToAction("Transaction");
        }

        // Ends a session via the page (called by JavaScript when the timer runs out or
        // when the librarian clicks "End Session" on the transaction card).
        // Returns a JSON result so the page can update without a full reload.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndSessionAjax(int sessionId)
        {
            var session = await _context.ComputerSessions
                .Include(s => s.ComputerUnit)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null) return Json(new { success = false });

            session.EndTime  = DateTime.Now;
            session.IsActive = false;

            // Make the computer available again for the next student
            bool computerExists = session.ComputerUnit != null;
            if (computerExists)
                session.ComputerUnit!.IsAvailable = true;

            await _context.SaveChangesAsync();
            await PushComputerEvent("ComputerSessionUpdated", new { action = "Ended", computerId = session.ComputerUnitId });

            return Json(new { success = true, computerId = session.ComputerUnitId });
        }

        // The kiosk page that runs fullscreen on each student computer in the lab.
        // No login needed — this page just displays the session timer and student info.
        // The `pc` parameter is the computer number (e.g. "PC-01") so the page knows which computer it is.
        [AllowAnonymous]
        [HttpGet]
        public IActionResult Kiosk(string pc)
        {
            ViewBag.PcNumber = pc;
            return View();
        }

        // Called by the kiosk page every few seconds to get the latest session info.
        // No login needed — runs on the student-facing computer.
        //
        // How remaining time is calculated:
        //   Total seconds = (base minutes + extra minutes added by librarian) × 60
        //   Remaining = total seconds − seconds already used
        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> KioskStatus(string pc)
        {
            var computer = await _context.ComputerUnits
                .Include(c => c.Sessions.Where(session => session.IsActive))
                    .ThenInclude(session => session.User)
                .FirstOrDefaultAsync(c => c.ComputerNumber == pc);

            if (computer == null) return Json(new { found = false });

            var activeSession = computer.Sessions.FirstOrDefault(session => session.IsActive);
            if (activeSession == null) return Json(new { isActive = false }); // No session running

            int totalSeconds   = (activeSession.AllowedMinutes + activeSession.ExtendedMinutes) * 60;
            int secondsElapsed = (int)(DateTime.Now - activeSession.StartTime).TotalSeconds;
            int secondsLeft    = totalSeconds - secondsElapsed;

            string studentName = activeSession.User != null
                ? activeSession.User.LastName + ", " + activeSession.User.FirstName
                : "";

            return Json(new
            {
                isActive  = true,
                remaining = secondsLeft,
                totalSecs = totalSeconds,
                name      = studentName,
                section   = activeSession.User?.Section ?? ""
            });
        }

        // Adds extra time to an active session.
        // The librarian can extend a session multiple times — the extra minutes stack.
        // The kiosk timer on the student's computer picks up the new total automatically.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ExtendTime(int sessionId, int minutes)
        {
            var session = await _context.ComputerSessions.FindAsync(sessionId);
            if (session == null) return NotFound();

            session.ExtendedMinutes += minutes; // Stacks on top of any previous extensions
            await _context.SaveChangesAsync();

            int totalMinutes = session.AllowedMinutes + session.ExtendedMinutes;
            return Json(new { success = true, totalMinutes });
        }

        // Ends a session the traditional way (full form POST, not AJAX).
        // Used as a fallback if the AJAX version doesn't work.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndSession(int sessionId)
        {
            var session = await _context.ComputerSessions
                .Include(s => s.ComputerUnit)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null) return NotFound();

            session.EndTime  = DateTime.Now;
            session.IsActive = false;

            if (session.ComputerUnit != null)
                session.ComputerUnit.IsAvailable = true;

            await _context.SaveChangesAsync();
            await PushComputerEvent("ComputerSessionUpdated", new { action = "Ended", computerId = session.ComputerUnitId });
            TempData["Success"] = "Session ended.";
            return RedirectToAction("Transaction");
        }

        // Hides a computer from the active list (marks it as archived with a reason).
        // Archived computers won't appear in the transaction page or be assignable to students.
        // Toggles a computer between Available and Unavailable (e.g., for maintenance).
        // Only works when the computer has no active session — can't mark a busy PC as unavailable.
        [HttpPost]
        public async Task<IActionResult> ToggleAvailability(int id)
        {
            var computer = await _context.ComputerUnits
                .Include(c => c.Sessions.Where(s => s.IsActive))
                .FirstOrDefaultAsync(c => c.Id == id);

            if (computer == null)
                return Json(new { success = false, message = "Computer not found." });

            bool hasActiveSession = computer.Sessions.Any(s => s.IsActive);
            if (hasActiveSession)
                return Json(new { success = false, message = "Cannot change availability while the computer has an active session." });

            computer.IsAvailable = !computer.IsAvailable;
            await _context.SaveChangesAsync();

            return Json(new { success = true, isAvailable = computer.IsAvailable });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Archive(int id, string reason)
        {
            var computer = await _context.ComputerUnits.FindAsync(id);
            if (computer == null) return NotFound();

            computer.IsArchived    = true;
            computer.IsAvailable   = false;
            computer.ArchiveReason = reason;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Computer archived.";
            return RedirectToAction("Index");
        }

        // Shows the list of archived computers.
        public async Task<IActionResult> ArchiveList()
        {
            await LoadComputerStats();

            var archivedComputers = await _context.ComputerUnits
                .Where(computer => computer.IsArchived)
                .OrderByDescending(computer => computer.CreatedAt)
                .ToListAsync();

            return View(archivedComputers);
        }

        // Permanently removes an archived computer from the database.
        // Only available for computers that are already archived, not active ones.
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

        // Brings an archived computer back to active status.
        // Sets it as available again and clears the archive reason.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(int id)
        {
            var computer = await _context.ComputerUnits.FindAsync(id);
            if (computer == null) return NotFound();

            computer.IsArchived    = false;
            computer.IsAvailable   = true;
            computer.ArchiveReason = "";

            await _context.SaveChangesAsync();
            TempData["Success"] = "Computer restored.";
            return RedirectToAction("ArchiveList");
        }
    }
}
