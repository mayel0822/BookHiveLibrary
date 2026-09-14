using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Data
{
    /// <summary>
    /// The main Entity Framework Core database context for BookHive.
    /// Extends IdentityDbContext so ASP.NET Identity tables (AspNetUsers, AspNetRoles, etc.)
    /// are automatically included alongside BookHive's custom tables.
    /// All DbSet properties map to tables in Azure SQL (or local SQL Server in dev).
    /// </summary>
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        /// <summary>OTP codes sent via email for email-based verification (legacy/admin login)</summary>
        public DbSet<OtpVerification> OtpVerifications { get; set; }

        /// <summary>Library book catalog</summary>
        public DbSet<Book> Books { get; set; }

        /// <summary>Book borrow/reservation records — tracks status from Pending to Returned</summary>
        public DbSet<BookReservation> BookReservations { get; set; }

        /// <summary>Physical computers in the library (PC-01, PC-02, etc.)</summary>
        public DbSet<ComputerUnit> ComputerUnits { get; set; }

        /// <summary>Individual student computer usage sessions with time tracking</summary>
        public DbSet<ComputerSession> ComputerSessions { get; set; }

        /// <summary>RFID tap-in / tap-out logs for library entry/exit tracking</summary>
        public DbSet<RFIDLog> RFIDLogs { get; set; }

        /// <summary>Class sections (e.g. BSIT 2-A) — students must be in a section to borrow books</summary>
        public DbSet<Section> Sections { get; set; }

        /// <summary>Direct messages between users (Students, Professors, Librarians)</summary>
        public DbSet<Message> Messages { get; set; }
    }
}
