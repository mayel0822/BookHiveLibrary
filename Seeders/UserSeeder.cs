using BookHiveLibrary.Constants;
using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;

namespace BookHiveLibrary.Seeders
{
    // Creates default admin/test accounts on first startup.
    // These accounts let the team log in immediately after deployment
    // without needing to create accounts manually.
    //
    // Default accounts created:
    //   misadmin             — MIS admin account
    //   marielle.cunanan     — MIS staff account
    //   librarian1           — Librarian account
    //   professor1           — Professor account
    //   student1             — Student account
    //
    // All accounts start with the password "Admin123!" and use the local login
    // (not Microsoft SSO) so they work even without Azure AD configured.
    public static class UserSeeder
    {
        public static async Task SeedUsersAsync(UserManager<ApplicationUser> userManager)
        {
            // Each entry is: (username, email, first name, last name, user type, role constant)
            var defaultAccounts = new[]
            {
                ("misadmin",         "misadmin@bookhive.local",         "MIS",       "Admin",    "MIS",       RoleConstants.MIS),
                ("marielle.cunanan", "marielle.cunanan@bookhive.local", "Marielle",  "Cunanan",  "MIS",       RoleConstants.MIS),
                ("librarian1",       "librarian1@bookhive.local",       "Librarian", "One",      "Librarian", RoleConstants.Librarian),
                ("professor1",       "professor1@bookhive.local",       "Professor", "One",      "Professor", RoleConstants.Professor),
                ("student1",         "student1@bookhive.local",         "Student",   "One",      "Student",   RoleConstants.Student),
            };

            foreach (var (username, email, firstName, lastName, userType, role) in defaultAccounts)
            {
                await CreateDefaultUserAsync(userManager, username, email, firstName, lastName, userType, role);
            }
        }

        // Creates a single default account — skips it if the username already exists.
        private static async Task CreateDefaultUserAsync(
            UserManager<ApplicationUser> userManager,
            string username,
            string email,
            string firstName,
            string lastName,
            string userType,
            string roleName)
        {
            // Only create the account if it doesn't already exist
            bool accountAlreadyExists = await userManager.FindByNameAsync(username) != null;
            if (accountAlreadyExists)
                return;

            var newUser = new ApplicationUser
            {
                UserName       = username,
                Email          = email,
                FirstName      = firstName,
                LastName       = lastName,
                UserType       = userType,
                IsActive       = true,
                EmailConfirmed = true,  // Skip email confirmation for seed accounts
                IsFirstLogin   = false  // Skip the first-login profile setup flow
            };

            // Create the account with the default password
            var createResult = await userManager.CreateAsync(newUser, "Admin123!");
            if (createResult.Succeeded)
            {
                // Assign the correct role so the Authorize attributes work
                await userManager.AddToRoleAsync(newUser, roleName);
                Console.WriteLine($"[Seeder] Created default account: {username} ({userType})");
            }
            else
            {
                string errors = string.Join(", ", createResult.Errors.Select(error => error.Description));
                Console.WriteLine($"[Seeder] Failed to create {username}: {errors}");
            }
        }
    }
}
