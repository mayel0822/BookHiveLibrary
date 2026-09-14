using BookHiveLibrary.Data;
using BookHiveLibrary.Models;
using BookHiveLibrary.Services;
using BookHiveLibrary.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Controllers
{
    // This controller handles everything that MIS (Management Information System) staff can do.
    // Only users with the MIS role can access these pages.
    //
    // MIS is responsible for managing all user accounts: registering them, editing their info,
    // deactivating/restoring accounts, generating temporary passwords, and importing users from Excel.
    [Authorize(Roles = "MIS")]
    public class MISController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly GraphService _graphService; // Used to reset passwords in Microsoft/Azure
        private readonly ApplicationDbContext _context;

        public MISController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, GraphService graphService, ApplicationDbContext context)
        {
            _userManager   = userManager;
            _signInManager = signInManager;
            _graphService  = graphService;
            _context       = context;
        }

        // This runs automatically before every action in this controller.
        // It checks if the current MIS user's account is still active.
        // If their account was turned off while they were logged in, they get signed out right away.
        // It also loads their name and profile picture into ViewBag for the page header.
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var user = await _userManager.GetUserAsync(context.HttpContext.User);
            if (user != null && !user.IsActive)
            {
                await _signInManager.SignOutAsync();
                context.Result = RedirectToAction("Index", "Home");
                return;
            }

            await SetCurrentUserViewBag();
            await next();
        }

        // Loads the logged-in MIS user's info into ViewBag so the layout header can display their name and picture.
        private async Task SetCurrentUserViewBag()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                ViewBag.CurrentUserFullName = !string.IsNullOrWhiteSpace(user.FirstName)
                    ? user.FirstName
                    : user.UserName;
                ViewBag.CurrentUserEmail = user.Email;
                ViewBag.CurrentUserPic   = user.ProfilePicture;
            }
        }

        // ── Dashboard ─────────────────────────────────────────────────────────

        // The MIS dashboard page.
        // Shows how many active librarians, students, and professors are in the system,
        // and the 8 most recently registered users.
        public async Task<IActionResult> Dashboard()
        {
            ViewBag.TotalLibrarians  = await _userManager.Users.CountAsync(u => u.UserType == "Librarian" && u.IsActive);
            ViewBag.TotalStudents    = await _userManager.Users.CountAsync(u => u.UserType == "Student"   && u.IsActive);
            ViewBag.TotalProfessors  = await _userManager.Users.CountAsync(u => u.UserType == "Professor" && u.IsActive);
            ViewBag.ActiveLibrarians = await _userManager.Users.CountAsync(u => u.UserType == "Librarian" && u.IsActive);
            ViewBag.ActiveStudents   = await _userManager.Users.CountAsync(u => u.UserType == "Student"   && u.IsActive);
            ViewBag.ActiveProfessors = await _userManager.Users.CountAsync(u => u.UserType == "Professor" && u.IsActive);

            ViewBag.RecentUsers = await _userManager.Users
                .Where(u => u.IsActive && (u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor"))
                .OrderByDescending(u => u.CreatedAt)
                .Take(8)
                .ToListAsync();

            return View();
        }

        // ── Registration ──────────────────────────────────────────────────────

        // Shows the registration page (has 3 tabs: single user form, Excel import, template download).
        [HttpGet]
        public IActionResult Registration() => View();

        // Creates one user account from the registration form.
        // A random password is generated automatically — students log in using their Microsoft account,
        // not this password. The email is also saved as the OutlookEmail so Microsoft password resets work.
        [HttpPost]
        public async Task<IActionResult> RegisterUser(
            string role, string firstName, string lastName, string middleName,
            string email, string studentNumber, string employeeNumber, string section,
            string rfidNumber, string adviserEmail, string? course, string? level)
        {
            if (await _userManager.FindByEmailAsync(email) != null)
            {
                TempData["RegError"] = "An account with that email already exists.";
                return RedirectToAction("Registration");
            }

            var username = email.Split('@')[0];
            var user = new ApplicationUser
            {
                UserName       = username,
                Email          = email,
                OutlookEmail   = email, // Saved so the Graph API can find this account in Azure
                FirstName      = firstName ?? "",
                MiddleName     = middleName ?? "",
                LastName       = lastName ?? "",
                UserType       = role,
                StudentNumber  = studentNumber ?? "",
                EmployeeNumber = employeeNumber ?? "",
                Section        = section ?? "",
                RFIDNumber     = rfidNumber ?? "",
                AdviserEmail   = adviserEmail ?? "",
                Course         = course ?? "",
                Level          = level ?? "",
                EmailConfirmed     = true,
                IsActive           = true,
                IsFirstLogin       = true,  // They'll set up their phone on first login
                RegistrationStatus = "Initial"
            };

            // A random password is generated — students use Microsoft login, not this password
            var randomPassword = "Bh!" + Guid.NewGuid().ToString("N")[..12];
            var result = await _userManager.CreateAsync(user, randomPassword);
            if (!result.Succeeded)
            {
                TempData["RegError"] = string.Join("; ", result.Errors.Select(e => e.Description));
                return RedirectToAction("Registration");
            }

            await _userManager.AddToRoleAsync(user, role);
            TempData["Success"] = $"{role} account for {firstName} {lastName} created successfully.";
            return RedirectToAction("Registration");
        }

        // ── User Information ──────────────────────────────────────────────────

        // Shows all active users (Librarian, Student, Professor) with optional role filter and search.
        public async Task<IActionResult> UserInformation(string? roleFilter, string? search)
        {
            var query = _userManager.Users
                .Where(u => u.IsActive &&
                    (u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor"));

            if (!string.IsNullOrWhiteSpace(roleFilter) && roleFilter != "All")
                query = query.Where(u => u.UserType == roleFilter);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u =>
                    u.FirstName.Contains(search)     ||
                    u.LastName.Contains(search)      ||
                    u.Email!.Contains(search)        ||
                    u.StudentNumber.Contains(search) ||
                    u.EmployeeNumber.Contains(search));

            var users = await query.OrderBy(u => u.LastName).ToListAsync();

            ViewBag.RoleFilter = string.IsNullOrWhiteSpace(roleFilter) ? "All" : roleFilter;
            ViewBag.Search = search;
            return View(users);
        }

        // ── Archived Users ────────────────────────────────────────────────────

        // Shows all deactivated users (their accounts are turned off but not deleted).
        public async Task<IActionResult> ArchivedUsers()
        {
            var users = await _userManager.Users
                .Where(u => !u.IsActive &&
                    (u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor"))
                .OrderBy(u => u.LastName)
                .ToListAsync();
            return View(users);
        }

        // Turns off a user's account. They won't be able to log in anymore.
        // If they're already logged in, they'll be signed out automatically on their next action.
        [HttpPost]
        public async Task<IActionResult> ArchiveUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                user.IsActive = false;
                await _userManager.UpdateAsync(user);
                TempData["Success"] = $"{user.FirstName} {user.LastName} has been archived.";
            }
            return RedirectToAction("UserInformation");
        }

        // Shows a deactivated user's profile along with their last 20 records from each category:
        // library entries (RFID taps), book reservations, and computer sessions.
        [HttpGet]
        public async Task<IActionResult> ArchivedDetail(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            ViewBag.RFIDLogs = await _context.RFIDLogs
                .Where(r => r.UserId == id)
                .OrderByDescending(r => r.TapInTime)
                .Take(20)
                .ToListAsync();

            ViewBag.BookReservations = await _context.BookReservations
                .Include(r => r.Book)
                .Where(r => r.UserId == id)
                .OrderByDescending(r => r.ReservationDate)
                .Take(20)
                .ToListAsync();

            ViewBag.ComputerSessions = await _context.ComputerSessions
                .Include(r => r.ComputerUnit)
                .Where(r => r.UserId == id)
                .OrderByDescending(r => r.StartTime)
                .Take(20)
                .ToListAsync();

            return View(user);
        }

        // Turns a deactivated user's account back on so they can log in again.
        [HttpPost]
        public async Task<IActionResult> RestoreUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                user.IsActive = true;
                await _userManager.UpdateAsync(user);
                TempData["Success"] = $"{user.FirstName} {user.LastName} has been restored.";
            }
            return RedirectToAction("ArchivedUsers");
        }

        // ── Role-specific Lists ───────────────────────────────────────────────

        // Shortcut methods that show a filtered list for each role
        public Task<IActionResult> Librarians(string? search) => UserList("Librarian", search);
        public Task<IActionResult> Students(string? search)   => UserList("Student",   search);
        public Task<IActionResult> Professors(string? search) => UserList("Professor",  search);

        private async Task<IActionResult> UserList(string role, string? search)
        {
            var query = _userManager.Users.Where(u => u.UserType == role);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u =>
                    u.FirstName.Contains(search)     ||
                    u.LastName.Contains(search)      ||
                    u.Email!.Contains(search)        ||
                    u.StudentNumber.Contains(search) ||
                    u.EmployeeNumber.Contains(search));

            var users = await query.OrderBy(u => u.LastName).ToListAsync();
            ViewBag.Role   = role;
            ViewBag.Search = search;
            return View("UserList", users);
        }

        // ── Excel Import ──────────────────────────────────────────────────────

        // Creates and downloads a ready-to-fill Excel template with the required columns and sample rows.
        // The RFID column is formatted as "Text" to prevent Excel from dropping leading zeros.
        [HttpPost]
        [HttpGet]
        public IActionResult DownloadTemplate()
        {
            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new OfficeOpenXml.ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("Users");

            var headers = new[] { "UserType", "FirstName", "MiddleName", "LastName", "Email", "Password", "Phone", "StudentNumber", "EmployeeNumber", "RFIDNumber" };
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.Cells[1, i + 1].Value = headers[i];
                sheet.Cells[1, i + 1].Style.Font.Bold = true;
            }

            // Sample rows to show MIS staff how to fill in the template
            object[,] samples = {
                { "Student",   "Juan",   "Santos", "Dela Cruz", "juan@school.onmicrosoft.com",    "Pass@1234", "09123456789", "2023-0001", "",         "00001" },
                { "Professor", "Maria",  "Lopez",  "Reyes",     "maria@school.onmicrosoft.com",   "Pass@1234", "09187654321", "",          "EMP-001",  "00002" },
                { "Librarian", "Jose",   "Cruz",   "Santos",    "jose@school.onmicrosoft.com",    "Pass@1234", "09111111111", "",          "LIB-001",  "00003" },
            };
            for (int r = 0; r < samples.GetLength(0); r++)
                for (int c = 0; c < samples.GetLength(1); c++)
                    sheet.Cells[r + 2, c + 1].Value = samples[r, c];

            // Format the RFID column as text so Excel doesn't remove leading zeros (e.g., "00001" stays "00001")
            var rfidCol = sheet.Cells[2, 10, 100, 10];
            rfidCol.Style.Numberformat.Format = "@";

            sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

            var bytes = package.GetAsByteArray();
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "BookHive_ImportTemplate.xlsx");
        }

        // Imports users from an uploaded Excel file.
        // Handles two situations:
        //   1. The MIS staff uploaded the file directly (reads it from the uploaded file)
        //   2. The MIS staff edited the preview table first (reads the edited rows sent from the browser)
        //
        // For each row, it checks:
        //   - UserType must be Student, Professor, or Librarian (skips rows with invalid types)
        //   - Email and Password must not be empty
        //   - Email must not already be in use
        public async Task<IActionResult> ImportExcel(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select an Excel file.";
                return RedirectToAction("Registration");
            }

            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            var results = new List<string[]>();

            // Check if the MIS staff submitted edited rows from the preview table
            var editedHeadersJson = Request.Form["editedHeaders"].FirstOrDefault();
            List<string[]>? editedDataRows = null;
            List<string>? editedHeaders = null;

            if (!string.IsNullOrEmpty(editedHeadersJson))
            {
                editedHeaders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(editedHeadersJson);
                editedDataRows = new List<string[]>();
                int ri = 0;
                while (Request.Form.ContainsKey($"editedRow_{ri}"))
                {
                    var rowJson = Request.Form[$"editedRow_{ri}"].FirstOrDefault();
                    if (rowJson != null)
                        editedDataRows.Add(System.Text.Json.JsonSerializer.Deserialize<string[]>(rowJson)!);
                    ri++;
                }
            }

            // Helper to get a cell value by column name from the edited rows
            Dictionary<string, int>? headerIndex = null;
            if (editedHeaders != null)
            {
                headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < editedHeaders.Count; i++)
                    if (!headerIndex.ContainsKey(editedHeaders[i]))
                        headerIndex[editedHeaders[i]] = i;
            }

            string CellFromEdited(string[] row, string name)
            {
                if (headerIndex == null || !headerIndex.TryGetValue(name, out int idx)) return "";
                return idx < row.Length ? row[idx].Trim() : "";
            }

            var validTypes = new[] { "Student", "Professor", "Librarian" };

            if (editedDataRows != null)
            {
                // Using the edited rows submitted from the browser preview
                foreach (var editedRow in editedDataRows)
                {
                    string userType    = CellFromEdited(editedRow, "UserType");
                    string firstName   = CellFromEdited(editedRow, "FirstName");
                    string middleName  = CellFromEdited(editedRow, "MiddleName");
                    string lastName    = CellFromEdited(editedRow, "LastName");
                    string email       = CellFromEdited(editedRow, "Email");
                    string password    = CellFromEdited(editedRow, "Password");
                    string phone       = CellFromEdited(editedRow, "Phone");
                    string studentNum  = CellFromEdited(editedRow, "StudentNumber");
                    string employeeNum = CellFromEdited(editedRow, "EmployeeNumber");
                    string rfid        = CellFromEdited(editedRow, "RFIDNumber");

                    string fullName = $"{firstName} {lastName}";
                    string idNum    = !string.IsNullOrEmpty(studentNum) ? studentNum : employeeNum;

                    if (!validTypes.Contains(userType, StringComparer.OrdinalIgnoreCase))
                    {
                        results.Add(new[] { userType, fullName, email, idNum, "Skipped (invalid UserType)" });
                        continue;
                    }
                    userType = validTypes.First(t => t.Equals(userType, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                    {
                        results.Add(new[] { userType, fullName, email, idNum, "Skipped (missing email or password)" });
                        continue;
                    }

                    var existing = await _userManager.FindByEmailAsync(email);
                    if (existing != null) { results.Add(new[] { userType, fullName, email, idNum, "Already exists" }); continue; }

                    var user = new ApplicationUser
                    {
                        UserName = email, Email = email, FirstName = firstName, LastName = lastName,
                        MiddleName = middleName, UserType = userType, StudentNumber = studentNum,
                        EmployeeNumber = employeeNum, PhoneNumber = phone, RFIDNumber = rfid,
                        IsActive = true, EmailConfirmed = true, IsFirstLogin = true,
                        CreatedAt = DateTime.Now, RegistrationStatus = "Initial"
                    };
                    var result = await _userManager.CreateAsync(user, password);
                    results.Add(new[] { userType, fullName, email, idNum,
                        result.Succeeded ? "Registered" : string.Join(", ", result.Errors.Select(e => e.Description)) });
                }
            }
            else
            {
                // Reading directly from the uploaded Excel file
                using var stream = new MemoryStream();
                await file.CopyToAsync(stream);
                using var package = new OfficeOpenXml.ExcelPackage(stream);

                // Use the sheet that has the most rows (handles files with multiple sheets)
                var sheet = package.Workbook.Worksheets
                    .OrderByDescending(s => s.Dimension?.Rows ?? 0)
                    .First();
                int rowCount = sheet.Dimension?.Rows ?? 0;
                int colCount = sheet.Dimension?.Columns ?? 0;

                // Build a lookup from column name to column number using row 1 as headers
                var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= colCount; c++)
                {
                    var h = sheet.Cells[1, c].Text.Trim();
                    if (!string.IsNullOrEmpty(h) && !headers.ContainsKey(h))
                        headers[h] = c;
                }

                string Cell(int row, string name)
                {
                    if (!headers.TryGetValue(name, out int col)) return "";
                    return sheet.Cells[row, col].Text.Trim();
                }

                // Process each data row (starting from row 2 since row 1 is the header)
                for (int row = 2; row <= rowCount; row++)
                {
                    string userType    = Cell(row, "UserType");
                    string firstName   = Cell(row, "FirstName");
                    string middleName  = Cell(row, "MiddleName");
                    string lastName    = Cell(row, "LastName");
                    string email       = Cell(row, "Email");
                    string password    = Cell(row, "Password");
                    string phone       = Cell(row, "Phone");
                    string studentNum  = Cell(row, "StudentNumber");
                    string employeeNum = Cell(row, "EmployeeNumber");
                    string rfid        = Cell(row, "RFIDNumber");

                    string fullName = $"{firstName} {lastName}";
                    string idNum    = !string.IsNullOrEmpty(studentNum) ? studentNum : employeeNum;

                    if (!validTypes.Contains(userType, StringComparer.OrdinalIgnoreCase))
                    {
                        results.Add(new[] { userType, fullName, email, idNum, "Skipped (invalid UserType — must be Student, Professor, or Librarian)" });
                        continue;
                    }

                    // Normalize casing to match exactly (e.g., "student" → "Student")
                    userType = validTypes.First(t => t.Equals(userType, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                    {
                        results.Add(new[] { userType, fullName, email, idNum, "Skipped (missing email or password)" });
                        continue;
                    }

                    var existing = await _userManager.FindByEmailAsync(email);
                    if (existing != null)
                    {
                        results.Add(new[] { userType, fullName, email, idNum, "Already exists" });
                        continue;
                    }

                    var user = new ApplicationUser
                    {
                        UserName       = email,
                        Email          = email,
                        FirstName      = firstName,
                        LastName       = lastName,
                        MiddleName     = middleName,
                        UserType       = userType,
                        StudentNumber  = studentNum,
                        EmployeeNumber = employeeNum,
                        PhoneNumber    = phone,
                        RFIDNumber     = rfid,
                        IsActive           = true,
                        EmailConfirmed     = true,
                        IsFirstLogin       = true,
                        CreatedAt          = DateTime.Now,
                        RegistrationStatus = "Initial"
                    };

                    var result = await _userManager.CreateAsync(user, password);
                    if (result.Succeeded)
                        await _userManager.AddToRoleAsync(user, userType);
                    results.Add(new[] { userType, fullName, email, idNum,
                        result.Succeeded ? "Registered" : string.Join(", ", result.Errors.Select(e => e.Description)) });
                }
            }

            int registered = results.Count(r => r[4] == "Registered");
            TempData["Success"] = $"Import complete, {registered} new user(s) has been successfully registered.";
            return RedirectToAction("Registration");
        }

        // ── Create (single user form) ─────────────────────────────────────────

        [HttpGet]
        public IActionResult Create(string role = "Student")
        {
            return View(new MISCreateUserViewModel { Role = role });
        }

        [HttpPost]
        public async Task<IActionResult> Create(MISCreateUserViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Use the email prefix as the username if no username was entered
            var username = string.IsNullOrWhiteSpace(model.Username)
                ? model.Email.Split('@')[0]
                : model.Username;

            var user = new ApplicationUser
            {
                UserName        = username,
                Email           = model.Email,
                OutlookEmail    = model.Email,
                FirstName       = model.FirstName,
                MiddleName      = model.MiddleName,
                LastName        = model.LastName,
                UserType        = model.Role,
                StudentNumber   = model.StudentNumber ?? "",
                EmployeeNumber  = model.EmployeeNumber ?? "",
                Section         = model.Section ?? "",
                RFIDNumber      = model.RFIDNumber ?? "",
                PhoneNumber     = model.PhoneNumber,
                AdviserEmail    = model.AdviserEmail,
                Level           = model.Level,
                Course          = model.Course,
                EmailConfirmed     = true,
                IsActive           = true,
                IsFirstLogin       = true,
                RegistrationStatus = "Initial"
            };

            var result = await _userManager.CreateAsync(user, model.Password);
            if (!result.Succeeded)
            {
                foreach (var e in result.Errors)
                    ModelState.AddModelError("", e.Description);
                return View(model);
            }

            await _userManager.AddToRoleAsync(user, model.Role);
            TempData["Success"] = $"{model.Role} account for {model.FirstName} {model.LastName} created successfully.";
            return RedirectToAction(model.Role + "s");
        }

        // ── User Detail Page ──────────────────────────────────────────────────

        // Maps short course codes to their full names.
        // For example "BSCS" becomes "Bachelor of Science in Computer Science".
        private static readonly Dictionary<string, string> CourseFullNames = new()
        {
            ["BSCS"]   = "Bachelor of Science in Computer Science",
            ["BSIT"]   = "Bachelor of Science in Information Technology",
            ["BSCpE"]  = "Bachelor of Science in Computer Engineering",
            ["BSBA"]   = "Bachelor of Science in Business Administration",
            ["BSRTCS"] = "Bachelor of Science in Retail Technology and Consumer Science",
            ["BACOMM"] = "Bachelor of Arts in Communication",
            ["BAP"]    = "Bachelor of Arts in Psychology",
            ["BSTM"]   = "Bachelor of Science in Tourism Management",
            ["ACT"]    = "2-yr. Associate in Computer Technology",
            ["ART"]    = "2-yr. Associate in Retail Technology",
            ["ABM"]    = "Accountancy, Business, and Management",
            ["STEM"]   = "Science, Technology, Engineering, and Mathematics",
            ["HUMSS"]  = "Humanities and Social Sciences",
            ["GA"]     = "General Academic",
            ["ICT"]    = "IT in Mobile App and Web Development",
            ["DA"]     = "Digital Arts",
        };

        // Shows a user's full profile detail page.
        //
        // Auto-fill: if the user has a section but their Course or Level is empty, those are
        // automatically filled in from the section record. This handles students who were
        // registered before Course/Level were added to sections.
        //
        // Also expands short course codes (like "BSCS") to full names before showing them.
        [HttpGet]
        public async Task<IActionResult> Detail(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            // If the user has a section but missing course/level, fill them in from the section record
            if (!string.IsNullOrEmpty(user.Section) &&
                (string.IsNullOrEmpty(user.Course) || string.IsNullOrEmpty(user.Level)))
            {
                var section = await _context.Sections.FirstOrDefaultAsync(s => s.SectionName == user.Section);
                if (section != null)
                {
                    if (string.IsNullOrEmpty(user.Course)) user.Course = section.Course;
                    if (string.IsNullOrEmpty(user.Level))  user.Level  = section.Level;
                    await _userManager.UpdateAsync(user);
                }
            }

            // Expand short course codes to full names
            if (!string.IsNullOrEmpty(user.Course) && CourseFullNames.TryGetValue(user.Course, out var fullName))
            {
                user.Course = fullName;
                await _userManager.UpdateAsync(user);
            }

            return View(user);
        }

        // Saves changes to a user's RFID number, section, course, and level from the detail page.
        [HttpPost]
        public async Task<IActionResult> SaveUserDetails(string id, string rfidNumber,
            string? section, string? course, string? level)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) { TempData["Error"] = "User not found."; return RedirectToAction("UserInformation"); }

            user.RFIDNumber = rfidNumber ?? "";
            if (section != null) user.Section = section;
            if (course  != null) user.Course  = course;
            if (level   != null) user.Level   = level;

            var result = await _userManager.UpdateAsync(user);
            if (result.Succeeded)
            {
                TempData["Success"]     = "User details updated successfully.";
                TempData["RfidSuccess"] = "true";
            }
            else
                TempData["Error"] = string.Join(", ", result.Errors.Select(e => e.Description));

            return RedirectToAction("Detail", new { id });
        }

        // Generates a temporary password for a student and resets both their:
        //   1. Local password (used for fallback login in the system)
        //   2. Microsoft/Azure password (used for the student's actual Microsoft SSO login)
        //
        // The password is randomly generated and shown to MIS once.
        // After logging in with the temporary password, the student is required to set a new one
        // (because IsFirstLogin is set to true).
        [HttpPost]
        public async Task<IActionResult> ChangeUserPassword(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            // Generate a random temporary password that meets complexity requirements
            string tempPassword = "Bh@" + Guid.NewGuid().ToString("N")[..8];

            var token  = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, tempPassword);

            if (result.Succeeded)
            {
                // Also reset the password in Microsoft/Azure so the student can log in via Microsoft
                try
                {
                    await _graphService.ResetPasswordAsync(user.Email!, tempPassword);
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Local password set but Microsoft account reset failed: {ex.Message}";
                    return RedirectToAction("Detail", new { id });
                }

                // Mark the student as needing to change their password on next login
                user.IsFirstLogin = true;
                await _userManager.UpdateAsync(user);

                TempData["Success"] = "Temporary password generated. Give this to the student — they will be asked to set a new password on next login.";
                TempData["GeneratedPassword"] = tempPassword; // This is shown once on the detail page
            }
            else
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));

            return RedirectToAction("Detail", new { id });
        }

        // ── My Profile ────────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Profile()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            return View(user);
        }

        // Saves changes to the MIS user's own profile (name, phone, profile picture).
        // The profile picture is converted to a text string and saved in the database
        // so we don't need a separate file storage service.
        [HttpPost]
        public async Task<IActionResult> Profile(string firstName, string lastName, string middleName, string phoneNumber, IFormFile? profilePicture)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            user.FirstName   = firstName   ?? user.FirstName;
            user.LastName    = lastName    ?? user.LastName;
            user.MiddleName  = middleName  ?? user.MiddleName;
            user.PhoneNumber = phoneNumber ?? user.PhoneNumber;

            if (profilePicture != null && profilePicture.Length > 0)
            {
                using var ms = new MemoryStream();
                await profilePicture.CopyToAsync(ms);
                var base64   = Convert.ToBase64String(ms.ToArray());
                var mimeType = profilePicture.ContentType;
                user.ProfilePicture = $"data:{mimeType};base64,{base64}";
            }

            await _userManager.UpdateAsync(user);
            TempData["Success"] = "Profile updated successfully.";
            return RedirectToAction("Profile");
        }

        // ── Turn Account On/Off ───────────────────────────────────────────────

        // Turns a user's account on or off.
        // If turned off, the user will be automatically signed out on their next action.
        // The optional "reason" message is stored and can be shown to explain the deactivation.
        // The "reason" is cleared when the account is turned back on.
        [HttpPost]
        public async Task<IActionResult> ToggleStatus(string id, string returnAction = "UserInformation", string? reason = null)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                user.IsActive = !user.IsActive;
                if (!user.IsActive && !string.IsNullOrWhiteSpace(reason))
                    user.DeactivationReason = reason;
                else if (user.IsActive)
                    user.DeactivationReason = null; // Clear the reason when re-activating

                await _userManager.UpdateAsync(user);

                var displayName = string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? user.UserName
                    : $"{user.FirstName} {user.LastName}".Trim();
                TempData["Success"] = $"{displayName} has been {(user.IsActive ? "activated" : "deactivated")}.";
            }
            return RedirectToAction(returnAction);
        }

        // ── Permanently Delete ────────────────────────────────────────────────

        // Permanently deletes a user account from the database.
        // Messages are deleted first because the database requires linked records to be removed first.
        // (RFID logs, book reservations, and computer sessions are kept for record-keeping.)
        [HttpPost]
        public async Task<IActionResult> Delete(string id, string returnAction, string? reason)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                var displayName = string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? user.UserName
                    : $"{user.FirstName} {user.LastName}".Trim();

                // Delete the user's messages first, otherwise the database won't allow deleting the user
                var messages = _context.Messages.Where(m => m.SenderId == id || m.ReceiverId == id);
                _context.Messages.RemoveRange(messages);
                await _context.SaveChangesAsync();

                await _userManager.DeleteAsync(user);
                TempData["Success"] = $"{displayName}'s account has been deleted.";
            }
            return RedirectToAction(returnAction);
        }

        // ── Fix Missing Roles ─────────────────────────────────────────────────

        // A one-time utility that fixes accounts where the role assignment is missing.
        // This can happen if a user has a UserType set but wasn't properly assigned their role.
        // Go to /MIS/RepairRoles to run it if users can't access their dashboards.
        public async Task<IActionResult> RepairRoles()
        {
            var users = await _userManager.Users.ToListAsync();
            int repaired = 0;
            foreach (var u in users)
            {
                if (string.IsNullOrEmpty(u.UserType)) continue;
                var roles = await _userManager.GetRolesAsync(u);
                if (!roles.Contains(u.UserType))
                {
                    await _userManager.AddToRoleAsync(u, u.UserType);
                    repaired++;
                }
            }
            TempData["Success"] = $"Role repair complete — {repaired} account(s) were fixed.";
            return RedirectToAction("UserInformation");
        }
    }
}
