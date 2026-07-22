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
            _userManager = userManager;
            _signInManager = signInManager;
            _context = context;
            _emailService = emailService;
            _smsService = smsService;
        }

        public IActionResult Login()
        {
            return RedirectToAction("Index", "Home");
        }

        public IActionResult AdminLogin()
        {
            return View();
        }

        public IActionResult VerifyOtp()
        {
            var model = new VerifyOtpViewModel
            {
                Email = TempData["Email"]?.ToString() ?? ""
            };

            TempData.Keep("Email");
            TempData.Keep("AccountEmail");
            TempData.Keep("PendingOutlookEmail");

            return View(model);
        }

        public IActionResult CompleteProfile()
        {
            TempData.Keep("Email");
            return View();
        }

        public IActionResult VerifyPhoneOtp()
        {
            var model = new VerifyPhoneOtpViewModel
            {
                Email = TempData["Email"]?.ToString() ?? ""
            };

            TempData.Keep("Email");

            return View(model);
        }

        // ── Microsoft OAuth ──────────────────────────────────────────────────

        public IActionResult MicrosoftLogin()
        {
            var redirectUrl = Url.Action("MicrosoftLoginCallback", "Account");
            var properties = _signInManager.ConfigureExternalAuthenticationProperties(
                "Microsoft", redirectUrl);
            properties.Parameters["prompt"] = "login";
            return Challenge(properties, "Microsoft");
        }

        public async Task<IActionResult> MicrosoftLoginCallback()
        {
            var info = await _signInManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                TempData["Error"] = "Microsoft login failed. Please try again.";
                return RedirectToAction("Login");
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email)
                     ?? info.Principal.FindFirstValue("preferred_username");

            if (string.IsNullOrEmpty(email))
            {
                TempData["Error"] = "Could not retrieve email from Microsoft account.";
                return RedirectToAction("Login");
            }

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
            {
                TempData["Error"] = "Your Microsoft account is not registered in the system. Please contact MIS to register your account first.";
                return RedirectToAction("Login");
            }

            if (!user.IsActive)
            {
                TempData["Error"] = "Your account has been deactivated. Please contact MIS.";
                return RedirectToAction("Login");
            }

            // All users: check if phone number is set up (first time login)
            if (string.IsNullOrEmpty(user.PhoneNumber))
            {
                TempData["Email"] = user.Email;
                return RedirectToAction("CompleteProfile");
            }

            // Send SMS OTP via Semaphore
            string otpCode = GenerateOtp();
            user.PhoneOTPCode = otpCode;
            user.PhoneOTPExpiration = DateTime.Now.AddMinutes(5);
            await _userManager.UpdateAsync(user);

            try
            {
                await _smsService.SendOtpAsync(user.PhoneNumber!, otpCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SMS ERROR] {ex.Message}");
                Console.WriteLine($"[DEV OTP] {user.Email} → {otpCode}");
            }

            TempData["Email"] = user.Email;
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Student/Professor Test Login ─────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> StudentLogin(LoginViewModel model)
        {
            ModelState.Remove("OutlookEmail");
            if (!ModelState.IsValid)
                return View("Login", model);

            ApplicationUser? user;

            if (model.EmailOrUsername.Contains("@"))
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                ModelState.AddModelError("", "No account found. Please contact MIS to register your account.");
                return View("Login", model);
            }

            if (user.UserType != "Student" && user.UserType != "Professor")
            {
                ModelState.AddModelError("", "This login is for Student and Professor accounts only.");
                return View("Login", model);
            }

            if (!user.IsActive)
            {
                ModelState.AddModelError("", "Your account has been deactivated. Please contact MIS or the librarian.");
                return View("Login", model);
            }

            var passwordCheck = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCheck)
            {
                ModelState.AddModelError("", "Password incorrect.");
                return View("Login", model);
            }

            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToDashboard(user.UserType);
        }

        // ── Admin (password) Login ───────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> AdminLogin(LoginViewModel model)
        {
            ModelState.Remove("OutlookEmail");
            if (!ModelState.IsValid)
                return View(model);

            ApplicationUser? user;

            if (model.EmailOrUsername.Contains("@"))
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                ModelState.AddModelError("", "User not found.");
                return View(model);
            }

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

            var passwordCheck = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCheck)
            {
                ModelState.AddModelError("", "Password incorrect.");
                return View(model);
            }

            // Sign in directly — Microsoft Authenticator handles MFA for real accounts via OAuth
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToDashboard(user.UserType);
        }

        // ── MIS Login ────────────────────────────────────────────────────────

        public IActionResult MISLogin()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> MISLogin(LoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            ApplicationUser? user;

            if (model.EmailOrUsername.Contains("@"))
                user = await _userManager.FindByEmailAsync(model.EmailOrUsername);
            else
                user = await _userManager.FindByNameAsync(model.EmailOrUsername);

            if (user == null)
            {
                ModelState.AddModelError("", "User not found.");
                return View(model);
            }

            if (user.UserType != "MIS")
            {
                ModelState.AddModelError("", "This login is for MIS accounts only.");
                return View(model);
            }

            var passwordCheck = await _userManager.CheckPasswordAsync(user, model.Password);
            if (!passwordCheck)
            {
                ModelState.AddModelError("", "Password incorrect.");
                return View(model);
            }

            if (string.IsNullOrEmpty(model.OutlookEmail))
            {
                ModelState.AddModelError("OutlookEmail", "Please enter your Microsoft Outlook email to receive the OTP.");
                return View(model);
            }

            string outlookEmail = model.OutlookEmail;

            string otpCode = GenerateOtp();
            _context.OtpVerifications.Add(new OtpVerification
            {
                Email = outlookEmail,
                Code = otpCode,
                ExpirationTime = DateTime.Now.AddMinutes(5),
                IsUsed = false
            });
            await _context.SaveChangesAsync();

            bool emailSent = false;
            try
            {
                await _emailService.SendOtpAsync(outlookEmail, otpCode);
                emailSent = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
            }

            TempData["OtpFallback"] = emailSent
                ? $"OTP sent to {outlookEmail}. Code: {otpCode}"
                : $"Email could not be sent. Your OTP code is: {otpCode}";

            TempData["Email"] = outlookEmail;
            TempData["AccountEmail"] = user.Email;
            TempData["PendingOutlookEmail"] = string.IsNullOrEmpty(user.OutlookEmail) ? outlookEmail : null;
            return RedirectToAction("VerifyOtp");
        }

        // ── Verify Email OTP (returning admin users) ─────────────────────────

        [HttpPost]
        public async Task<IActionResult> VerifyOtp(VerifyOtpViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var otp = _context.OtpVerifications
                .Where(x => x.Email == model.Email && x.Code == model.Code && !x.IsUsed)
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (otp == null)
            {
                ModelState.AddModelError("", "Invalid OTP.");
                return View(model);
            }

            if (otp.ExpirationTime < DateTime.Now)
            {
                ModelState.AddModelError("", "OTP has expired.");
                return View(model);
            }

            otp.IsUsed = true;
            await _context.SaveChangesAsync();

            // Find user by the account email stored at login time, then fallback lookups
            var accountEmail = TempData["AccountEmail"]?.ToString();
            var user = (!string.IsNullOrEmpty(accountEmail) ? await _userManager.FindByEmailAsync(accountEmail) : null)
                    ?? _userManager.Users.FirstOrDefault(u => u.OutlookEmail == model.Email)
                    ?? await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return RedirectToAction("Login");

            // Save OutlookEmail to DB now that OTP is verified successfully
            var pendingOutlook = TempData["PendingOutlookEmail"]?.ToString();
            if (!string.IsNullOrEmpty(pendingOutlook) && string.IsNullOrEmpty(user.OutlookEmail))
            {
                user.OutlookEmail = pendingOutlook;
                await _userManager.UpdateAsync(user);
            }

            if (!user.IsActive)
            {
                TempData["Error"] = "Your account has been deactivated. Please contact MIS.";
                return RedirectToAction("Login");
            }

            await _signInManager.SignInAsync(user, isPersistent: false);

            // First-time login: collect phone number before going to dashboard
            if (user.IsFirstLogin)
            {
                TempData["Email"] = user.Email;
                return RedirectToAction("CompleteProfile");
            }

            return RedirectToDashboard(user.UserType);
        }

        // ── Complete Profile (first-time login) ──────────────────────────────

        [HttpPost]
        [HttpPost]
        public async Task<IActionResult> CompleteProfile(string phoneNumber)
        {
            var email = TempData["Email"]?.ToString();
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Login");

            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return RedirectToAction("Login");

            // Save phone number first
            user.PhoneNumber = phoneNumber;
            await _userManager.UpdateAsync(user);

            // Generate OTP and send via SMS to verify ownership before saving for reminders
            string otpCode = GenerateOtp();
            user.PhoneOTPCode = otpCode;
            user.PhoneOTPExpiration = DateTime.Now.AddMinutes(5);
            await _userManager.UpdateAsync(user);

            try
            {
                await _smsService.SendOtpAsync(phoneNumber, otpCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine("==========================================");
                Console.WriteLine($"  SMS FAILED: {ex.Message}");
                Console.WriteLine($"  DEV OTP CODE: {otpCode}");
                Console.WriteLine("==========================================");
                TempData["SmsError"] = $"SMS could not be delivered. DEV CODE: {otpCode}";
            }

            TempData["Email"] = email;
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Verify Phone OTP (first-time login) ──────────────────────────────

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

            if (user.PhoneOTPCode != model.Code)
            {
                ModelState.AddModelError("", "Invalid verification code.");
                return View(model);
            }

            if (user.PhoneOTPExpiration < DateTime.Now)
            {
                ModelState.AddModelError("", "Verification code has expired. Please go back and re-enter your number.");
                return View(model);
            }

            if (!user.IsActive)
            {
                ModelState.AddModelError("", "Your account has been deactivated. Please contact MIS.");
                return View(model);
            }

            // Phone is confirmed — mark it verified and complete first-login setup
            user.PhoneVerified = true;
            user.IsFirstLogin = false;
            user.PhoneOTPCode = "";
            await _userManager.UpdateAsync(user);

            await _signInManager.SignInAsync(user, isPersistent: false);

            return RedirectToDashboard(user.UserType);
        }

        // ── Resend Phone OTP ─────────────────────────────────────────────────

        [HttpPost]
        public async Task<IActionResult> ResendPhoneOtp(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return RedirectToAction("Login");

            string otpCode = GenerateOtp();
            user.PhoneOTPCode = otpCode;
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

            TempData["Email"] = email;
            TempData["Success"] = "A new code has been sent to your phone.";
            return RedirectToAction("VerifyPhoneOtp");
        }

        // ── Logout ───────────────────────────────────────────────────────────

        public async Task<IActionResult> Logout()
        {
            await _signInManager.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string GenerateOtp()
        {
            return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        }

        private IActionResult RedirectToDashboard(string userType) => userType switch
        {
            "MIS" => RedirectToAction("Dashboard", "MIS"),
            "Librarian" => RedirectToAction("Dashboard", "Librarian"),
            "Student" => RedirectToAction("Dashboard", "Student"),
            "Professor" => RedirectToAction("Dashboard", "Student"),
            _ => RedirectToAction("Index", "Home")
        };
    }
}
