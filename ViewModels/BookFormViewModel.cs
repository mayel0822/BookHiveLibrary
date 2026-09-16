using System.ComponentModel.DataAnnotations;

namespace BookHiveLibrary.ViewModels
{
    public class BookFormViewModel
    {
        public int Id { get; set; }

        // Must contain at least one letter (blocks pure-digit garbage like "212" or
        // "12312"), but digits are still allowed alongside letters — a strict
        // "no digits at all" rule would also reject real titles like "Catch-22",
        // "Fahrenheit 451", and "2001: A Space Odyssey". The one real title this
        // still blocks: a book titled with bare digits and nothing else (1984, by
        // George Orwell, being the famous example) — a narrow, deliberate tradeoff.
        [Required]
        [RegularExpression(@"^(?=.*[A-Za-z])[A-Za-z0-9\s.,'"":;!?()&\-]+$", ErrorMessage = "Title must include actual letters, not just numbers/symbols.")]
        public string Title { get; set; } = "";

        [Required]
        [RegularExpression(@"^(?=.*[A-Za-z])[A-Za-z0-9\s.,'"":;!?()&\-]+$", ErrorMessage = "Author must include actual letters, not just numbers/symbols.")]
        public string Author { get; set; } = "";

        [Required]
        public string Category { get; set; } = "";

        public string GradeLevel { get; set; } = "";

        // Digits only. Note: this is a deliberate change from the free-form call
        // numbers the Book model's own doc comment used as an example
        // ("REF 620 B12") — if the library actually uses a classification scheme
        // like Dewey/LC rather than plain sequential numbering, this needs
        // loosening. Only affects new registrations; existing non-numeric call
        // numbers already in the database aren't touched by this.
        [Required(ErrorMessage = "Call No. is required.")]
        [RegularExpression(@"^[0-9]+$", ErrorMessage = "Call No. must contain digits only.")]
        public string CallNumber { get; set; } = "";

        // Optional — no [Required], and the field's own "e.g. ..." placeholder
        // signals it's a hint, not a requirement — so empty is allowed, but if
        // something is entered it must look like an ISBN: digits and hyphens only,
        // matching the "978-3-16-148410-0" format shown in that placeholder.
        [Display(Name = "ISBN")]
        [RegularExpression(@"^[0-9\-]*$", ErrorMessage = "ISBN must contain only digits and hyphens (e.g. 978-3-16-148410-0).")]
        public string ISBN { get; set; } = "";

        [Required]
        public string AisleLocation { get; set; } = "";

        // Same rule as Title/Author (must contain a letter, digits still allowed
        // alongside them), but optional — empty is allowed.
        [RegularExpression(@"^$|^(?=.*[A-Za-z])[A-Za-z0-9\s.,'"":;!?()&\-]+$", ErrorMessage = "Synopsis must include actual letters, not just numbers/symbols.")]
        public string Description { get; set; } = "";

        [Display(Name = "Cover Image URL")]
        public string CoverImageUrl { get; set; } = "";

        // Optional, same reasoning as ISBN above. When provided, must be a plain
        // 4-digit year — not a range, not "circa", not text.
        [Display(Name = "Published Year")]
        [RegularExpression(@"^([0-9]{4})?$", ErrorMessage = "Published Year must be a 4-digit year (e.g. 2023).")]
        public string PublishedYear { get; set; } = "";

        [Range(1, 1000)]
        public int TotalQuantity { get; set; } = 1;

        [Range(0, 1000)]
        public int AvailableQuantity { get; set; } = 1;

        public string BookFor { get; set; } = "Student";
        public bool IsRoomUseOnly { get; set; } = false;
    }
}
