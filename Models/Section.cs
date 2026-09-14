namespace BookHiveLibrary.Models
{
    /// <summary>
    /// Represents a class section in the school (e.g. "BSIT 2-A").
    /// Created and managed by the Librarian in the Sectioning page.
    /// Students are assigned to sections; unassigned students cannot borrow books.
    /// When a section is deleted, assigned students are deactivated automatically.
    /// </summary>
    public class Section
    {
        public int Id { get; set; }

        /// <summary>Academic level: "Junior High School", "Senior High School", or "Tertiary"</summary>
        public string Level { get; set; } = "";

        /// <summary>Full course name (e.g. "Bachelor of Science in Information Technology")</summary>
        public string Course { get; set; } = "";

        /// <summary>Year within the course (e.g. "1st Year", "2nd Year")</summary>
        public string Year { get; set; } = "";

        /// <summary>The section label shown to users (e.g. "BSIT 2-A")</summary>
        public string SectionName { get; set; } = "";

        /// <summary>Name of the adviser/homeroom teacher for this section (for overdue notifications)</summary>
        public string AdviserName { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
