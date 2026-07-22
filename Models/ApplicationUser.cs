using Microsoft.AspNetCore.Identity;

namespace BookHiveLibrary.Models
{
    public class ApplicationUser : IdentityUser
    {
        public string FirstName { get; set; } = "";

        public string MiddleName { get; set; } = "";

        public string LastName { get; set; } = "";

        public string UserType { get; set; } = "";

        public string StudentNumber { get; set; } = "";

        public string EmployeeNumber { get; set; } = "";

        public string Section { get; set; } = "";

        public string RFIDNumber { get; set; } = "";

        public bool IsFirstLogin { get; set; } = true;

        public bool EmailVerifiedCustom { get; set; }

        public bool PhoneVerified { get; set; }

        public string OTPCode { get; set; } = "";

        public DateTime OTPExpiration { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string PhoneOTPCode { get; set; } = "";
        public DateTime PhoneOTPExpiration { get; set; }

        public string OutlookEmail { get; set; } = "";

        public string AdviserEmail { get; set; } = "";

        public string Level { get; set; } = "";

        public string Course { get; set; } = "";

        public string? ProfilePicture { get; set; }

        public string? DeactivationReason { get; set; }
    }
}