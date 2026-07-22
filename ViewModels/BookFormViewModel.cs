using System.ComponentModel.DataAnnotations;

namespace BookHiveLibrary.ViewModels
{
    public class BookFormViewModel
    {
        public int Id { get; set; }

        [Required]
        public string Title { get; set; } = "";

        [Required]
        public string Author { get; set; } = "";

        [Required]
        public string Category { get; set; } = "";

        public string GradeLevel { get; set; } = "";

        [Required]
        public string CallNumber { get; set; } = "";

        [Display(Name = "ISBN")]
        public string ISBN { get; set; } = "";

        [Required]
        public string AisleLocation { get; set; } = "";

        public string Description { get; set; } = "";

        [Display(Name = "Cover Image URL")]
        public string CoverImageUrl { get; set; } = "";

        [Display(Name = "Published Year")]
        public string PublishedYear { get; set; } = "";

        [Range(1, 1000)]
        public int TotalQuantity { get; set; } = 1;

        [Range(0, 1000)]
        public int AvailableQuantity { get; set; } = 1;

        public string BookFor { get; set; } = "Student";
        public bool IsRoomUseOnly { get; set; } = false;
    }
}
