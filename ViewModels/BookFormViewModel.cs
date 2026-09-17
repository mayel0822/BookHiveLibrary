using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BookHiveLibrary.ViewModels
{
    public class BookFormViewModel : IValidatableObject
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

        // Exactly 5 digits (#####). Note: this is a deliberate change from the
        // free-form call numbers the Book model's own doc comment used as an
        // example ("REF 620 B12") — if the library actually uses a
        // classification scheme like Dewey/LC rather than plain sequential
        // numbering, this needs loosening. Only affects new registrations;
        // existing call numbers already in the database aren't touched by this.
        [Required(ErrorMessage = "Call No. is required.")]
        [RegularExpression(@"^[0-9]{5}$", ErrorMessage = "Call No. must be exactly 5 digits.")]
        public string CallNumber { get; set; } = "";

        // Optional — no [Required], and the field's own "e.g. ..." placeholder
        // signals it's a hint, not a requirement — so empty is allowed, but if
        // something is entered it must be a full 13-digit ISBN-13 in the
        // 3-1-2-6-1 hyphenated grouping shown in that placeholder
        // (978-3-16-148410-0), not just any digits-and-hyphens string.
        [Display(Name = "ISBN")]
        [RegularExpression(@"^$|^\d{3}-\d{1}-\d{2}-\d{6}-\d{1}$", ErrorMessage = "ISBN must be 13 digits in the format 978-3-16-148410-0.")]
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

        // Beyond the 4-digit format above, a published year still can't be in
        // the future — [RegularExpression] alone can't compare against
        // "the current year" since that's not a fixed pattern.
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!string.IsNullOrEmpty(PublishedYear)
                && int.TryParse(PublishedYear, out int year)
                && year > DateTime.Now.Year)
            {
                yield return new ValidationResult(
                    $"Published Year cannot be later than {DateTime.Now.Year}.",
                    new[] { nameof(PublishedYear) });
            }
        }
    }
}
