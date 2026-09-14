namespace BookHiveLibrary.Constants
{
    /// <summary>
    /// Centralized role name constants used throughout the application.
    /// NOTE: The values here are the ASP.NET Identity role names stored in AspNetRoles.
    /// MIS and Librarian use UPPERCASE in [Authorize] attributes (e.g. Roles = "LIBRARIAN").
    /// Student and Professor use UPPERCASE as well.
    /// These constants must match exactly what's seeded by RoleSeeder.
    /// </summary>
    public static class RoleConstants
    {
        public const string MIS       = "MIS";
        public const string Librarian = "LIBRARIAN";
        public const string Student   = "STUDENT";
        public const string Professor = "PROFESSOR";
    }
}
