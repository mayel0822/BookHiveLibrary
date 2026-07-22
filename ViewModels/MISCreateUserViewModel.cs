using System.ComponentModel.DataAnnotations;

namespace BookHiveLibrary.ViewModels
{
    public class MISCreateUserViewModel
    {
        [Required]
        public string Role { get; set; } = "";

        [Required, Display(Name = "First Name")]
        public string FirstName { get; set; } = "";

        [Display(Name = "Middle Name")]
        public string MiddleName { get; set; } = "";

        [Required, Display(Name = "Last Name")]
        public string LastName { get; set; } = "";

        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Display(Name = "Username")]
        public string Username { get; set; } = "";

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required, DataType(DataType.Password), Compare("Password")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = "";

        [Display(Name = "Student Number")]
        public string StudentNumber { get; set; } = "";

        [Display(Name = "Employee Number")]
        public string? EmployeeNumber { get; set; }

        [Display(Name = "Section / Department")]
        public string? Section { get; set; }

        [Display(Name = "RFID Number")]
        public string? RFIDNumber { get; set; }

        [Phone, Display(Name = "Phone Number")]
        public string? PhoneNumber { get; set; }

        [EmailAddress, Display(Name = "Adviser Email")]
        public string? AdviserEmail { get; set; }

        [Display(Name = "Level")]
        public string Level { get; set; } = "";

        [Display(Name = "Course / Strand")]
        public string Course { get; set; } = "";
    }
}
