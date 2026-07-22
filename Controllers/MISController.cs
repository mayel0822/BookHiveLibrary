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
    [Authorize(Roles = "MIS")]
    public class MISController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly GraphService _graphService;
        private readonly ApplicationDbContext _context;

        public MISController(UserManager<ApplicationUser> userManager, GraphService graphService, ApplicationDbContext context)
        {
            _userManager  = userManager;
            _graphService = graphService;
            _context      = context;
        }

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            await SetCurrentUserViewBag();
            await next();
        }

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

        // ── Dashboard ────────────────────────────────────────────────────────

        public async Task<IActionResult> Dashboard()
        {
            ViewBag.TotalLibrarians  = await _userManager.Users.CountAsync(u => u.UserType == "Librarian");
            ViewBag.TotalStudents    = await _userManager.Users.CountAsync(u => u.UserType == "Student");
            ViewBag.TotalProfessors  = await _userManager.Users.CountAsync(u => u.UserType == "Professor");
            ViewBag.ActiveLibrarians = await _userManager.Users.CountAsync(u => u.UserType == "Librarian" && u.IsActive);
            ViewBag.ActiveStudents   = await _userManager.Users.CountAsync(u => u.UserType == "Student"   && u.IsActive);
            ViewBag.ActiveProfessors = await _userManager.Users.CountAsync(u => u.UserType == "Professor" && u.IsActive);

            ViewBag.RecentUsers = await _userManager.Users
                .Where(u => u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor")
                .OrderByDescending(u => u.CreatedAt)
                .Take(8)
                .ToListAsync();

            return View();
        }

        // ── Registration (3-tab page) ─────────────────────────────────────────

        [HttpGet]
        public IActionResult Registration() => View();

        [HttpPost]
        public async Task<IActionResult> RegisterUser(
            string role, string firstName, string lastName, string middleName,
            string email, string studentNumber, string employeeNumber, string section,
            string rfidNumber, string adviserEmail)
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
                OutlookEmail   = email,
                FirstName      = firstName ?? "",
                MiddleName     = middleName ?? "",
                LastName       = lastName ?? "",
                UserType       = role,
                StudentNumber  = studentNumber ?? "",
                EmployeeNumber = employeeNumber ?? "",
                Section        = section ?? "",
                RFIDNumber     = rfidNumber ?? "",
                AdviserEmail   = adviserEmail ?? "",
                EmailConfirmed = true,
                IsActive       = true,
                IsFirstLogin   = true
            };

            // Generate a random internal password — users log in via Microsoft, not password
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

        // ── User Information (unified list with role filter) ─────────────────

        public async Task<IActionResult> UserInformation(string? roleFilter, string? search)
        {
            var query = _userManager.Users
                .Where(u => u.IsActive &&
                    (u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor"));

            if (!string.IsNullOrWhiteSpace(roleFilter) && roleFilter != "All")
                query = query.Where(u => u.UserType == roleFilter);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u =>
                    u.FirstName.Contains(search) ||
                    u.LastName.Contains(search)  ||
                    u.Email!.Contains(search)    ||
                    u.StudentNumber.Contains(search) ||
                    u.EmployeeNumber.Contains(search));

            var users = await query.OrderBy(u => u.LastName).ToListAsync();

            ViewBag.RoleFilter = string.IsNullOrWhiteSpace(roleFilter) ? "All" : roleFilter;
            ViewBag.Search = search;
            return View(users);
        }

        // ── Archived Users ────────────────────────────────────────────────────

        public async Task<IActionResult> ArchivedUsers()
        {
            var users = await _userManager.Users
                .Where(u => !u.IsActive &&
                    (u.UserType == "Librarian" || u.UserType == "Student" || u.UserType == "Professor"))
                .OrderBy(u => u.LastName)
                .ToListAsync();
            return View(users);
        }

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

        // ── List by role (kept for backward compat) ───────────────────────────

        public Task<IActionResult> Librarians(string? search) => UserList("Librarian", search);
        public Task<IActionResult> Students(string? search)   => UserList("Student",   search);
        public Task<IActionResult> Professors(string? search) => UserList("Professor",  search);

        private async Task<IActionResult> UserList(string role, string? search)
        {
            var query = _userManager.Users.Where(u => u.UserType == role);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(u =>
                    u.FirstName.Contains(search) ||
                    u.LastName.Contains(search)  ||
                    u.Email!.Contains(search)    ||
                    u.StudentNumber.Contains(search) ||
                    u.EmployeeNumber.Contains(search));

            var users = await query.OrderBy(u => u.LastName).ToListAsync();

            ViewBag.Role   = role;
            ViewBag.Search = search;
            return View("UserList", users);
        }

        // ── Excel Import ─────────────────────────────────────────────────────

        [HttpPost]
        [HttpGet]
        public IActionResult DownloadTemplate()
        {
            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new OfficeOpenXml.ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("Users");

            // Headers
            var headers = new[] { "UserType", "FirstName", "MiddleName", "LastName", "Email", "Password", "Phone", "StudentNumber", "EmployeeNumber", "RFIDNumber" };
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.Cells[1, i + 1].Value = headers[i];
                sheet.Cells[1, i + 1].Style.Font.Bold = true;
            }

            // Sample rows
            object[,] samples = {
                { "Student",   "Juan",   "Santos", "Dela Cruz", "juan@school.onmicrosoft.com",    "Pass@1234", "09123456789", "2023-0001", "",         "00001" },
                { "Professor", "Maria",  "Lopez",  "Reyes",     "maria@school.onmicrosoft.com",   "Pass@1234", "09187654321", "",          "EMP-001",  "00002" },
                { "Librarian", "Jose",   "Cruz",   "Santos",    "jose@school.onmicrosoft.com",    "Pass@1234", "09111111111", "",          "LIB-001",  "00003" },
            };
            for (int r = 0; r < samples.GetLength(0); r++)
                for (int c = 0; c < samples.GetLength(1); c++)
                    sheet.Cells[r + 2, c + 1].Value = samples[r, c];

            // Format RFIDNumber column (col 10) as Text to preserve leading zeros
            var rfidCol = sheet.Cells[2, 10, 100, 10];
            rfidCol.Style.Numberformat.Format = "@";

            sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

            var bytes = package.GetAsByteArray();
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "BookHive_ImportTemplate.xlsx");
        }

        public async Task<IActionResult> ImportExcel(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select an Excel file.";
                return RedirectToAction("Registration");
            }

            OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            var results = new List<string[]>();

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            using var package = new OfficeOpenXml.ExcelPackage(stream);
            // Pick the first sheet that has more than 1 row (data + headers)
            var sheet = package.Workbook.Worksheets
                .OrderByDescending(s => s.Dimension?.Rows ?? 0)
                .First();
            int rowCount = sheet.Dimension?.Rows ?? 0;
            int colCount = sheet.Dimension?.Columns ?? 0;

            // Build header→column index map from row 1 (case-insensitive)
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

            var validTypes = new[] { "Student", "Professor", "Librarian" };

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

                // Normalize casing
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
                    IsActive       = true,
                    EmailConfirmed = true,
                    IsFirstLogin   = true,
                    CreatedAt      = DateTime.Now
                };

                var result = await _userManager.CreateAsync(user, password);
                results.Add(new[] { userType, fullName, email, idNum,
                    result.Succeeded ? "Registered" : string.Join(", ", result.Errors.Select(e => e.Description)) });
            }

            int registered = results.Count(r => r[4] == "Registered");
            TempData["Success"] = $"Import complete, {registered} new user(s) has been successfully registered.";

            return RedirectToAction("Registration");
        }

        // ── Create ───────────────────────────────────────────────────────────

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
                EmailConfirmed  = true,
                IsActive        = true,
                IsFirstLogin    = true
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

        // ── Detail ───────────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Detail(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> ChangeUserPassword(string id, string newPassword)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

            if (result.Succeeded)
                TempData["Success"] = "Password updated successfully.";
            else
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));

            return RedirectToAction("Detail", new { id });
        }

        // ── Edit ─────────────────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var vm = new MISEditUserViewModel
            {
                Id             = user.Id,
                Role           = user.UserType,
                FirstName      = user.FirstName,
                MiddleName     = user.MiddleName,
                LastName       = user.LastName,
                Email          = user.Email ?? "",
                StudentNumber  = user.StudentNumber,
                EmployeeNumber = user.EmployeeNumber,
                Section        = user.Section,
                RFIDNumber     = user.RFIDNumber,
                PhoneNumber    = user.PhoneNumber ?? "",
                AdviserEmail   = user.AdviserEmail,
                Level          = user.Level,
                Course         = user.Course
            };

            return View(vm);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(MISEditUserViewModel model)
        {
            // Password fields are optional on edit
            if (!string.IsNullOrWhiteSpace(model.NewPassword) && model.NewPassword != model.ConfirmPassword)
                ModelState.AddModelError("ConfirmPassword", "Passwords do not match.");

            ModelState.Remove("Password");
            ModelState.Remove("ConfirmPassword");

            if (!ModelState.IsValid)
                return View(model);

            var user = await _userManager.FindByIdAsync(model.Id);
            if (user == null) return NotFound();

            user.FirstName      = model.FirstName;
            user.MiddleName     = model.MiddleName;
            user.LastName       = model.LastName;
            user.Email          = model.Email;
            user.OutlookEmail   = model.Email;
            user.StudentNumber  = model.StudentNumber;
            user.EmployeeNumber = model.EmployeeNumber;
            user.Section        = model.Section;
            user.RFIDNumber     = model.RFIDNumber;
            user.PhoneNumber    = model.PhoneNumber;
            user.AdviserEmail   = model.AdviserEmail;
            user.Level          = model.Level;
            user.Course         = model.Course;

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                foreach (var e in updateResult.Errors)
                    ModelState.AddModelError("", e.Description);
                return View(model);
            }

            if (!string.IsNullOrWhiteSpace(model.NewPassword))
            {
                try
                {
                    await _graphService.ResetPasswordAsync(user.Email!, model.NewPassword);
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", $"Failed to reset Microsoft account password: {ex.Message}");
                    return View(model);
                }
            }

            bool passwordChanged = !string.IsNullOrWhiteSpace(model.NewPassword);
            TempData["Success"] = passwordChanged
                ? $"{user.FirstName} {user.LastName}'s account saved and Microsoft password reset. They must change it on next login."
                : $"{user.FirstName} {user.LastName}'s account has been successfully saved.";
            return RedirectToAction("Edit", new { id = user.Id });
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

        // ── Toggle Active/Inactive ────────────────────────────────────────────

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
                    user.DeactivationReason = null;
                await _userManager.UpdateAsync(user);
                var displayName = string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? user.UserName
                    : $"{user.FirstName} {user.LastName}".Trim();
                TempData["Success"] = $"{displayName} has been {(user.IsActive ? "activated" : "deactivated")}.";
            }
            return RedirectToAction(returnAction);
        }

        // ── Delete (hard) ─────────────────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> Delete(string id, string returnAction, string? reason)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                var displayName = string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? user.UserName
                    : $"{user.FirstName} {user.LastName}".Trim();

                // Remove related records to avoid foreign key constraint errors
                var messages = _context.Messages.Where(m => m.SenderId == id || m.ReceiverId == id);
                _context.Messages.RemoveRange(messages);
                await _context.SaveChangesAsync();

                await _userManager.DeleteAsync(user);
                TempData["Success"] = $"{displayName}'s account has been deleted.";
            }
            return RedirectToAction(returnAction);
        }
    }
}
