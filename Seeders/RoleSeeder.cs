using BookHiveLibrary.Constants;
using Microsoft.AspNetCore.Identity;

namespace BookHiveLibrary.Seeders
{
    // Creates the four application roles in the database on first startup.
    // Roles are used to control who can access what (Authorize attributes on controllers).
    //
    // The four roles in BookHive:
    //   MIS       — manages users and accounts
    //   LIBRARIAN — manages books, borrows, and computer sessions
    //   STUDENT   — students who borrow books
    //   PROFESSOR — teaching staff (same access as students in most places)
    public static class RoleSeeder
    {
        public static async Task SeedRolesAsync(RoleManager<IdentityRole> roleManager)
        {
            // The complete list of roles the system needs
            string[] allRoles = new[]
            {
                RoleConstants.MIS,
                RoleConstants.Librarian,
                RoleConstants.Student,
                RoleConstants.Professor
            };

            foreach (string roleName in allRoles)
            {
                bool roleAlreadyExists = await roleManager.RoleExistsAsync(roleName);
                if (!roleAlreadyExists)
                {
                    await roleManager.CreateAsync(new IdentityRole(roleName));
                }
            }
        }
    }
}
