using System.ComponentModel.DataAnnotations;

namespace BookHiveLibrary.ViewModels
{
    public class MISEditUserViewModel
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";

        [Required, Display(Name = "First Name")]
        public string FirstName { get; set; } = "";

        [Display(Name = "Middle Name")]
        public string MiddleName { get; set; } = "";

        [Required, Display(Name = "Last Name")]
        public string LastName { get; set; } = "";

        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Display(Name = "Student Number")]
        public string StudentNumber { get; set; } = "";

        [Display(Name = "Employee Number")]
        public string EmployeeNumber { get; set; } = "";

        [Display(Name = "Section / Department")]
        public string Section { get; set; } = "";

        [Display(Name = "RFID Number")]
        public string RFIDNumber { get; set; } = "";

        [Display(Name = "Phone Number")]
        public string PhoneNumber { get; set; } = "";

        // Optional: leave blank to keep existing password
        [DataType(DataType.Password), Display(Name = "New Password (leave blank to keep)")]
        public string? NewPassword { get; set; }

        [DataType(DataType.Password), Compare("NewPassword"), Display(Name = "Confirm New Password")]
        public string? ConfirmPassword { get; set; }

        [EmailAddress, Display(Name = "Adviser Email")]
        public string AdviserEmail { get; set; } = "";

        [Display(Name = "Level")]
        public string Level { get; set; } = "";

        [Display(Name = "Course / Strand")]
        public string Course { get; set; } = "";
    }
}
