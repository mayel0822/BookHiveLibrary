using Microsoft.Graph;
using Azure.Identity;

namespace BookHiveLibrary.Services
{
    // This service resets a user's password in Microsoft Azure Active Directory (Azure AD).
    // It's used by MIS when they click "Reset Password" on a student's account page.
    //
    // How it works:
    //   1. MIS types in a new temporary password in BookHive
    //   2. This service calls the Microsoft Graph API to update the Azure AD password
    //   3. The next time the student logs in with Microsoft, they'll be asked to change it
    //
    // IMPORTANT: The ClientSecret must NEVER be committed to GitHub.
    //            Always set "ClientSecret": "" in appsettings.json before pushing to Git.
    //            Restore it locally after pushing.
    //
    // If any of the three Azure settings (ClientId, ClientSecret, TenantId) are missing,
    // the Graph client is not created and password reset will fail gracefully.
    public class GraphService
    {
        private readonly GraphServiceClient? _graphClient;

        public GraphService(IConfiguration configuration)
        {
            // Read Azure AD credentials from appsettings.json
            string? clientId     = configuration["AzureAd:ClientId"];
            string? clientSecret = configuration["AzureAd:ClientSecret"];
            string? tenantId     = configuration["AzureAd:TenantId"];

            // Only build the Graph client if all three values are present
            // (ClientSecret is left blank in source control for security)
            bool azureCredentialsAreConfigured =
                !string.IsNullOrEmpty(clientId)     &&
                !string.IsNullOrEmpty(clientSecret) &&
                !string.IsNullOrEmpty(tenantId);

            if (azureCredentialsAreConfigured)
            {
                // Use a client secret credential — this authenticates the app itself (not a user)
                var azureCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                _graphClient        = new GraphServiceClient(azureCredential);
            }
        }

        // Resets the Azure AD password for the user with the given email address.
        // After this call, the student will be required to change their password on next login.
        //
        // The matching logic: Microsoft stores users by UPN (usually their work email),
        // so we search by UPN to find the correct Azure AD user object.
        public async Task ResetPasswordAsync(string userEmail, string newPassword)
        {
            if (_graphClient == null)
                throw new InvalidOperationException("Microsoft Graph is not configured. ClientSecret may be missing.");

            // Look up the Azure AD user by their email/UPN
            var azureUser = await _graphClient.Users[userEmail].GetAsync();

            if (azureUser == null)
                throw new Exception($"No Azure AD user found with email: {userEmail}");

            // Prepare the password update — ForceChangePasswordNextSignIn means
            // they must set a new permanent password the next time they log in.
            var passwordUpdate = new Microsoft.Graph.Models.User
            {
                PasswordProfile = new Microsoft.Graph.Models.PasswordProfile
                {
                    Password                      = newPassword,
                    ForceChangePasswordNextSignIn = true
                }
            };

            // Patch (partially update) the user's profile in Azure AD
            await _graphClient.Users[azureUser.Id].PatchAsync(passwordUpdate);
        }
    }
}
