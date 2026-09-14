using Microsoft.AspNetCore.Identity;

namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Extends the default ASP.NET Identity user with BookHive-specific fields.
    /// Stored in the AspNetUsers table in Azure SQL.
    /// UserType determines which module/dashboard the user accesses: Student, Professor, Librarian, MIS.
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        // ── Name fields ──────────────────────────────────────────────────────

        public string FirstName { get; set; } = "";

        public string MiddleName { get; set; } = "";

        public string LastName { get; set; } = "";

        // ── Role / classification ─────────────────────────────────────────────

        /// <summary>
        /// Determines dashboard access: "Student", "Professor", "Librarian", "MIS"
        /// </summary>
        public string UserType { get; set; } = "";

        // ── School identifiers ────────────────────────────────────────────────

        public string StudentNumber { get; set; } = "";

        public string EmployeeNumber { get; set; } = "";

        /// <summary>Section name (e.g. "BSIT 2-A")</summary>
        public string Section { get; set; } = "";

        /// <summary>RFID card number used for library kiosk tap-in/tap-out</summary>
        public string RFIDNumber { get; set; } = "";

        // ── Login flow flags ──────────────────────────────────────────────────

        /// <summary>
        /// True on first login — triggers the phone number setup + OTP verification flow.
        /// Also set back to true by MIS when a temporary password is generated,
        /// so the student is forced to change it on next login.
        /// </summary>
        public bool IsFirstLogin { get; set; } = true;

        // ── Email OTP (used for admin/returning user verification) ────────────

        public bool EmailVerifiedCustom { get; set; }

        public string OTPCode { get; set; } = "";

        public DateTime OTPExpiration { get; set; }

        // ── SMS / Phone OTP (used after Microsoft SSO login) ──────────────────

        public bool PhoneVerified { get; set; }

        public string PhoneOTPCode { get; set; } = "";

        public DateTime PhoneOTPExpiration { get; set; }

        // ── Account status ────────────────────────────────────────────────────

        /// <summary>
        /// False = deactivated by MIS. Deactivated users are force-signed-out on every request.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string? DeactivationReason { get; set; }

        // ── Email addresses ───────────────────────────────────────────────────

        /// <summary>
        /// The student's Microsoft/Outlook email (used for Azure AD login and Graph API password reset).
        /// Different from the primary Email field which may be a school email.
        /// </summary>
        public string OutlookEmail { get; set; } = "";

        /// <summary>Adviser's email — used to send overdue book notifications</summary>
        public string AdviserEmail { get; set; } = "";

        // ── Academic info ─────────────────────────────────────────────────────

        /// <summary>Year level (e.g. "1st Year", "2nd Year")</summary>
        public string Level { get; set; } = "";

        public string Course { get; set; } = "";

        // ── Profile ───────────────────────────────────────────────────────────

        /// <summary>URL or path to the user's profile picture (from Microsoft account photo)</summary>
        public string? ProfilePicture { get; set; }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>
        /// Tracks where the user is in the registration process.
        /// "Initial" = just registered by MIS, "Completed" = phone verified and profile done.
        /// </summary>
        public string RegistrationStatus { get; set; } = "Initial";
    }
}
