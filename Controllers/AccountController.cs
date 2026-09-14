using BookHiveLibrary.Data;
using BookHiveLibrary.Models;
using BookHiveLibrary.Services;
using BookHiveLibrary.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;

namespace BookHiveLibrary.Controllers
{
    // This controller handles all login and logout flows in BookHive:
    //   - Microsoft account login (for Students and Professors)
    //   - Password login (for Librarians and MIS)
    //   - Phone OTP verification (text message code sent after Microsoft login)
    //   - Email OTP verification (for admin/returning users)
    //   - First-time profile setup (collecting the student's phone number)
    //   - Logout
    public class AccountController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ApplicationDbContext _context;
        private readonly EmailService _emailService;
        private readonly SmsService _smsService;

        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext context,
            EmailService emailService,
            SmsService smsService)
        {
            _userManager   = userManager;
            _signInManager = signInManager;
            _context       = context;
            _emailService  = emailService;
            _smsService    = smsService;
        }

        // Redirect /Account/Login to the home page (the login buttons are on the homepage)
        public IActionResult Login()
        {
            return RedirectToAction("Index", "Home");
        }

        // Shows the Librarian password login form
        public IActionResult AdminLogin()
        {
            return View();
        }

        // Shows the email OTP verification form (used after Librarian login)
        public IActionResult VerifyOtp()
        {
            var model = new VerifyOtpViewModel
            {
                Email = TempData["Email"]?.ToString() ?? ""
            };

            // Keep TempData alive so the next POST can still read these values
            TempData.Keep("Email");
            TempData.Keep("AccountEmail");
            TempData.Keep("PendingOutlookEmail");

            return View(model);
        }

        // Shows the phone number collection form (only shown on first login)
        public IActionResult CompleteProfile()
        {
            TempData.Keep("Email");
            return View();
        }

        // Shows the SMS OTP verification form (shown after the student enters their phone number)
        public IActionResult VerifyPhoneOtp()
        {
            var model = new VerifyPhoneOtpViewModel
            {
                Email = TempData["Email"]?.ToString() ?? ""
            };

            TempData.Keep("Email");

            return View(model);
        }

        // ── Microsoft Login (for Students and Professors) ─────────────────────

        // Starts the Microsoft account login flow.
        // "loginType" tells us which button the user clicked (student, librarian, or mis)
        // so we can check they're logging in with the right type of account.
        // "prompt=login" forces Microsoft to always show the account picker.
        public IActionResult MicrosoftLogin(string loginType = "student")
        {
            TempData["LoginType"] = loginType;

            string redirectUrl   = Url.Action("MicrosoftLoginCallback", "Account")!;
            var authProperties   = _signInManager.ConfigureExternalAuthenticationProperties("Microsoft", redirectUrl);
            authProperties.Parameters["prompt"] = "login"; // Always show Microsoft account picker

            return Challenge(authProperties, "Microsoft");
        }

        // Microsoft redirects the user here after they authenticate.
        // This checks:
        //   - The user's account exists in BookHive (only MIS-registered accounts can log in)
        //   - The account is still active (not deactivated)
        //   - The role matches the login button they clicked (e.g. a student can't log in as a librarian)
        // Then sends a phone OTP for verification (or logs in directly if MIS).
        public async Task<IActionResult> MicrosoftLoginCallback()
        {
            var loginInfo = await _signInManager.GetExternalLoginInfoAsync();
            if (loginInfo == null)
            {
                TempData["Error"] = "Microsoft login failed. Please try again.";
                return RedirectToAction("Login");
            }

            // Get the email from Microsoft's response
            string? email = loginInfo.Principal.FindFirstValue(ClaimTypes.Email)
                         ?? loginInfo.Principal.FindFirstValue("preferred_username");

            if (string.IsNullOrEmpty(email))
            {
                TempData["Error"] = "Could not retrieve email from Microsoft account.";
                return RedirectToAction("Login");
            }

            // Only users registered by MIS can log in
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                TempData["Error"] = "Your Microsoft account is not registered in the system. Please contact MIS to register your account first.";
                TempData["Unauthorized"] = "true";
                return RedirectToAction("Index", "Home");
            }

            // Deactivated users cannot log in
            if (!user.IsActive)
            {
                TempData["Error"] = "Your account has been deactivated. Please contact MIS.";
                TempData["Unauthorized"] = "true";
                return RedirectToAction("Index", "Home");
            }

            // Check the login type matches the user's actual role
            string loginType   = TempData["LoginType"]?.ToString() ?? "student";
            bool wrongRole     = loginType switch
            {
                "student"   => user.UserType != "Student" && user.UserType != "Professor",
                "librarian" => user.UserType != "Librarian",
                "mis"       => user.UserType != "MIS",
                _           => false
            };

            if (wrongRole)
            {
                TempData["Unauthorized"]    = "true";
                TempData["UnauthorizedMsg"] = "Unauthorized to log in here. Please use the correct login button for your account.";
                return RedirectToAction("Index", "Home");
            }

            // MIS users log in directly — no phone verification step
            bool isMisUser = user.UserType == "MIS";
            if (isMisUser)
            {
                await _signInManager.SignInAsync(user, isPersistent: false);
                return RedirectToAction("Dashboard", "MIS");
            }

            // First-time login: ask the student to enter their phone number first
            bool phoneNotSetUp = string.IsNullOrEmpty(user.PhoneNumber);
            if (phoneNotSetUp)
            {
                TempData["Email"] = user.Email;
                return RedirectToAction("CompleteProfile");
            }

            // Generate a 6-digit OTP and send it via SMS for phone verification
            string otpCode = GenerateOtp();
            user.PhoneOTPCode       = otpCode;
            user.PhoneOTPExpiration = DateTime.Now.AddMinutes(5);
            await _userManager.UpdateAsync(user);

            try
            {
                await _smsService.SendOtpAsync(user.PhoneNumber!, otpCode);
            }
            catch (Exception ex)
            {
                // If SMS fails, log to console (OTP is printed for dev/testing purposes)
                Console.WriteLine($"[SMS ERROR] {ex.Message}");
                Console.WriteLine($"[DEV OTP] {user.Email} → {otpCode}");
            }

            TempData["Email"] = user.Email;
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Student/Professor Password Login (fallback / testing) ─────────────

        // Password login for Students and Professors.
        // Used as a fallback when Microsoft SSO is not available (e.g. local testing).
        // Locks the account after 5 wrong attempts (for 1 hour).
        [HttpPost]
        public async Task<IActionResult> StudentLogin(LoginViewModel model)
        {
            ModelState.Remove("OutlookEmail");
            if (!ModelState.IsValid)
                return View("Login", model);

            // Support both email and username
            ApplicationUser? user;
            bool usedEmail = model.EmailOrUsername.Contains("@");
            if (usedEmail)
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                ModelState.AddModelError("", "No account found. Please contact MIS to register your account.");
                return View("Login", model);
            }

            bool wrongUserType = user.UserType != "Student" && user.UserType != "Professor";
            if (wrongUserType)
            {
                ModelState.AddModelError("", "This login is for Student and Professor accounts only.");
                return View("Login", model);
            }

            if (!user.IsActive)
            {
                ModelState.AddModelError("", "Your account has been deactivated. Please contact MIS or the librarian.");
                return View("Login", model);
            }

            bool accountIsLocked = await _userManager.IsLockedOutAsync(user);
            if (accountIsLocked)
            {
                ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                return View("Login", model);
            }

            bool passwordCorrect = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCorrect)
            {
                // Record the failed attempt; lock if they've reached the limit
                await _userManager.AccessFailedAsync(user);

                int maxAttempts   = _userManager.Options.Lockout.MaxFailedAccessAttempts;
                int failedSoFar   = await _userManager.GetAccessFailedCountAsync(user);
                int attemptsLeft  = maxAttempts - failedSoFar;

                bool nowLocked = await _userManager.IsLockedOutAsync(user);
                if (nowLocked)
                    ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                else
                    ModelState.AddModelError("", $"Password incorrect. {attemptsLeft} attempt(s) remaining.");

                return View("Login", model);
            }

            // Login successful — reset the failed attempt counter and sign in
            await _userManager.ResetAccessFailedCountAsync(user);
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToDashboard(user.UserType);
        }

        // ── Librarian Password Login ──────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> AdminLogin(LoginViewModel model)
        {
            ModelState.Remove("OutlookEmail");
            if (!ModelState.IsValid)
                return View(model);

            // Support both email and username
            ApplicationUser? user;
            bool usedEmail = model.EmailOrUsername.Contains("@");
            if (usedEmail)
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                ModelState.AddModelError("", "User not found.");
                return View(model);
            }

            // Only Librarian accounts can use this form
            if (user.UserType != "Librarian")
            {
                ModelState.AddModelError("", "This login is for Librarian accounts only.");
                return View(model);
            }

            if (!user.IsActive)
            {
                ModelState.AddModelError("", "Your account has been deactivated.");
                return View(model);
            }

            bool accountIsLocked = await _userManager.IsLockedOutAsync(user);
            if (accountIsLocked)
            {
                ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                return View(model);
            }

            bool passwordCorrect = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCorrect)
            {
                await _userManager.AccessFailedAsync(user);

                int maxAttempts  = _userManager.Options.Lockout.MaxFailedAccessAttempts;
                int failedSoFar  = await _userManager.GetAccessFailedCountAsync(user);
                int attemptsLeft = maxAttempts - failedSoFar;

                bool nowLocked = await _userManager.IsLockedOutAsync(user);
                if (nowLocked)
                    ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                else
                    ModelState.AddModelError("", $"Password incorrect. {attemptsLeft} attempt(s) remaining.");

                return View(model);
            }

            await _userManager.ResetAccessFailedCountAsync(user);
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToDashboard(user.UserType);
        }

        // ── MIS Password Login ────────────────────────────────────────────────

        public IActionResult MISLogin()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> MISLogin(LoginViewModel model)
        {
            ModelState.Remove("OutlookEmail");
            if (!ModelState.IsValid)
                return View(model);

            // Support both email and username
            ApplicationUser? user;
            bool usedEmail = model.EmailOrUsername.Contains("@");
            if (usedEmail)
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                TempData["Error"] = "Account not found.";
                return View(model);
            }

            // Only MIS accounts can use this form
            if (user.UserType != "MIS")
            {
                TempData["Error"] = "This login is for MIS accounts only.";
                return View(model);
            }

            if (!user.IsActive)
            {
                TempData["Error"] = "Your account has been deactivated.";
                return View(model);
            }

            bool accountIsLocked = await _userManager.IsLockedOutAsync(user);
            if (accountIsLocked)
            {
                TempData["Error"] = "Account locked due to too many failed attempts. Try again in 1 hour.";
                return View(model);
            }

            bool passwordCorrect = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCorrect)
            {
                await _userManager.AccessFailedAsync(user);

                int maxAttempts  = _userManager.Options.Lockout.MaxFailedAccessAttempts;
                int failedSoFar  = await _userManager.GetAccessFailedCountAsync(user);
                int attemptsLeft = maxAttempts - failedSoFar;

                bool nowLocked = await _userManager.IsLockedOutAsync(user);
                if (nowLocked)
                    TempData["Error"] = "Account locked due to too many failed attempts. Try again in 1 hour.";
                else
                    TempData["Error"] = $"Incorrect password. {attemptsLeft} attempt(s) remaining.";

                return View(model);
            }

            await _userManager.ResetAccessFailedCountAsync(user);
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToAction("Dashboard", "MIS");
        }

        // ── Verify Email OTP ──────────────────────────────────────────────────

        // Validates the OTP the user received via email.
        // Marks it as used so it can't be used again.
        // If it's the user's first login, sends them to the profile setup page.
        [HttpPost]
        public async Task<IActionResult> VerifyOtp(VerifyOtpViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            // Find the most recent unused OTP for this email + code combination
            var matchingOtp = _context.OtpVerifications
                .Where(otp => otp.Email == model.Email && otp.Code == model.Code && !otp.IsUsed)
                .OrderByDescending(otp => otp.Id)
                .FirstOrDefault();

            if (matchingOtp == null)
            {
                ModelState.AddModelError("", "Invalid OTP.");
                return View(model);
            }

            bool otpExpired = matchingOtp.ExpirationTime < DateTime.Now;
            if (otpExpired)
            {
                ModelState.AddModelError("", "OTP has expired.");
                return View(model);
            }

            // Mark as used so it cannot be reused
            matchingOtp.IsUsed = true;
            await _context.SaveChangesAsync();

            // Find the user — try account email, then OutlookEmail lookup, then direct match
            string? accountEmail = TempData["AccountEmail"]?.ToString();
            var user = (!string.IsNullOrEmpty(accountEmail) ? await _userManager.FindByEmailAsync(accountEmail) : null)
                    ?? _userManager.Users.FirstOrDefault(u => u.OutlookEmail == model.Email)
                    ?? await _userManager.FindByEmailAsync(model.Email);

            if (user == null)
                return RedirectToAction("Login");

            // If first time logging in with this Microsoft email, save it to the user's record
            string? pendingOutlookEmail = TempData["PendingOutlookEmail"]?.ToString();
            bool firstTimeOutlookLink   = !string.IsNullOrEmpty(pendingOutlookEmail) && string.IsNullOrEmpty(user.OutlookEmail);
            if (firstTimeOutlookLink)
            {
                user.OutlookEmail = pendingOutlookEmail!;
                await _userManager.UpdateAsync(user);
            }

            if (!user.IsActive)
            {
                TempData["Error"] = "Your account has been deactivated. Please contact MIS.";
                return RedirectToAction("Index", "Home");
            }

            await _signInManager.SignInAsync(user, isPersistent: false);

            // First-time login: ask for phone number before going to dashboard
            bool needsPhoneSetup = user.IsFirstLogin;
            if (needsPhoneSetup)
            {
                TempData["Email"] = user.Email;
                return RedirectToAction("CompleteProfile");
            }

            return RedirectToDashboard(user.UserType);
        }

        // ── First-Time Profile Setup ──────────────────────────────────────────

        // Saves the phone number the user entered on their first login,
        // then sends an SMS OTP so they can prove they own the number.
        [HttpPost]
        [HttpPost]
        public async Task<IActionResult> CompleteProfile(string phoneNumber)
        {
            string? email = TempData["Email"]?.ToString();
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Login");

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return RedirectToAction("Login");

            // Save the phone number to the user's profile
            user.PhoneNumber = phoneNumber;
            await _userManager.UpdateAsync(user);

            // Generate a 6-digit code and send it to their phone
            string otpCode = GenerateOtp();
            user.PhoneOTPCode       = otpCode;
            user.PhoneOTPExpiration = DateTime.Now.AddMinutes(5);
            await _userManager.UpdateAsync(user);

            try
            {
                await _smsService.SendOtpAsync(phoneNumber, otpCode);
            }
            catch (Exception ex)
            {
                // If SMS fails, show the code in the console for developers
                Console.WriteLine("==========================================");
                Console.WriteLine($"  SMS FAILED: {ex.Message}");
                Console.WriteLine($"  DEV OTP CODE: {otpCode}");
                Console.WriteLine("==========================================");
                TempData["SmsError"] = $"SMS could not be delivered. DEV CODE: {otpCode}";
            }

            TempData["Email"] = email;
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Verify Phone OTP ──────────────────────────────────────────────────

        // Validates the SMS code the user received on their phone.
        // On success: marks their phone as verified, clears the first-login flag,
        // signs them in, and sends them to their dashboard.
        [HttpPost]
        public async Task<IActionResult> VerifyPhoneOtp(VerifyPhoneOtpViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                ModelState.AddModelError("", "User not found.");
                return View(model);
            }

            bool accountIsLocked = await _userManager.IsLockedOutAsync(user);
            if (accountIsLocked)
            {
                ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                return View(model);
            }

            bool codeExpired = user.PhoneOTPExpiration < DateTime.Now;
            if (codeExpired)
            {
                ModelState.AddModelError("", "Verification code has expired. Please go back and re-enter your number.");
                return View(model);
            }

            bool codeIsWrong = user.PhoneOTPCode != model.Code;
            if (codeIsWrong)
            {
                // Wrong code — increment the failed attempt counter
                await _userManager.AccessFailedAsync(user);

                int maxAttempts  = _userManager.Options.Lockout.MaxFailedAccessAttempts;
                int failedSoFar  = await _userManager.GetAccessFailedCountAsync(user);
                int attemptsLeft = maxAttempts - failedSoFar;

                bool nowLocked = await _userManager.IsLockedOutAsync(user);
                if (nowLocked)
                    ModelState.AddModelError("", "Account locked due to too many failed attempts. Try again in 1 hour.");
                else
                    ModelState.AddModelError("", $"Invalid verification code. {attemptsLeft} attempt(s) remaining.");

                return View(model);
            }

            if (!user.IsActive)
            {
                ModelState.AddModelError("", "Your account has been deactivated. Please contact MIS.");
                return View(model);
            }

            // Code is correct — reset the lockout counter
            await _userManager.ResetAccessFailedCountAsync(user);

            // Mark their phone as verified and clear the first-login flag
            user.PhoneVerified  = true;
            user.IsFirstLogin   = false;
            user.PhoneOTPCode   = ""; // Clear the used code
            await _userManager.UpdateAsync(user);

            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToDashboard(user.UserType);
        }

        // ── Resend Phone OTP ──────────────────────────────────────────────────

        // Generates a new OTP and resends it via SMS.
        // Called when the user clicks "Resend Code" on the verification page.
        [HttpPost]
        public async Task<IActionResult> ResendPhoneOtp(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return RedirectToAction("Login");

            string otpCode = GenerateOtp();
            user.PhoneOTPCode       = otpCode;
            user.PhoneOTPExpiration = DateTime.Now.AddMinutes(5);
            await _userManager.UpdateAsync(user);

            try
            {
                await _smsService.SendOtpAsync(user.PhoneNumber!, otpCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SMS ERROR] {ex.Message}");
                TempData["SmsError"] = $"SMS could not be delivered. DEV CODE: {otpCode}";
            }

            TempData["Email"]   = email;
            TempData["Success"] = "A new code has been sent to your phone.";
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Logout ────────────────────────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        // Generates a random 6-digit code for OTP verification.
        // Uses a secure random number generator (not the regular Random class).
        private static string GenerateOtp()
        {
            int sixDigitCode = RandomNumberGenerator.GetInt32(100000, 999999);
            return sixDigitCode.ToString();
        }

        // Sends the user to the correct dashboard page based on their role.
        // Professors share the Student dashboard.
        private IActionResult RedirectToDashboard(string userType)
        {
            return userType switch
            {
                "MIS"       => RedirectToAction("Dashboard", "MIS"),
                "Librarian" => RedirectToAction("Dashboard", "Librarian"),
                "Student"   => RedirectToAction("Dashboard", "Student"),
                "Professor" => RedirectToAction("Dashboard", "Student"), // Professors use the Student module
                _           => RedirectToAction("Index", "Home")
            };
        }
    }
}
