using BookHiveLibrary.Models;
using Microsoft.AspNetCore.Identity;

namespace BookHiveLibrary.Seeders
{
    public static class UserSeeder
    {
        public static async Task SeedUsersAsync(
            UserManager<ApplicationUser> userManager)
        {
            await CreateUser(
            userManager,
            "misadmin",
            "MIS@elleiramnananuc04outlook.onmicrosoft.com",
            "MisBookHivePassword@123",
            "MIS");

            await CreateUser(
            userManager,
            "marielle.cunanan",
            "elleiram.nananuc04@outlook.com",
            "Ajie0403.",
            "MIS",
            "Marielle",
            "Cunanan");

            await CreateUser(
                userManager,
                "librarian1",
                "librarian@bookhive.com",
                "LibrarianPassword@123",
                "Librarian",
                "Librarian",
                "BookHive");

            await CreateUser(
                userManager,
                "professor1",
                "professor@bookhive.com",
                "ProfessorPassword@123",
                "Professor",
                "Professor",
                "BookHive");

            await CreateUser(
                userManager,
                "student1",
                "Rebisco2023@outlook.com",
                "StudentPassword@123",
                "Student",
                "Student",
                "BookHive");
        }

        private static async Task CreateUser(
        UserManager<ApplicationUser> userManager,
        string username,
        string email,
        string password,
        string role,
        string firstName = "",
        string lastName = "")
        {
            var user = await userManager.FindByEmailAsync(email);

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = username,
                    Email = email,
                    UserType = role,
                    FirstName = firstName,
                    LastName = lastName,
                    EmailConfirmed = true,
                    IsFirstLogin = false
                };

                Console.WriteLine($"Creating {role}: {email}");

                var result = await userManager.CreateAsync(user, password);

                if (result.Succeeded)
                {
                    Console.WriteLine($"{role} created successfully.");

                    await userManager.AddToRoleAsync(user, role);
                }
                else
                {
                    Console.WriteLine($"{role} creation failed.");

                    foreach (var error in result.Errors)
                    {
                        Console.WriteLine(error.Code + " : " + error.Description);
                    }
                }
            }
        }
    }
}