using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace BookHiveLibrary.Services
{
    public class GraphService
    {
        private GraphServiceClient? _graphClient;

        public GraphService(IConfiguration configuration)
        {
            var clientId     = configuration["AzureAd:ClientId"] ?? "";
            var clientSecret = configuration["AzureAd:ClientSecret"] ?? "";
            var tenantId     = configuration["AzureAd:TenantId"] ?? "";

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret) && !string.IsNullOrEmpty(tenantId))
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                _graphClient = new GraphServiceClient(credential);
            }
        }

        public async Task ResetPasswordAsync(string userEmail, string newPassword)
        {
            if (_graphClient == null)
                throw new Exception("Microsoft Graph is not configured. Check AzureAd settings.");

            var user = await _graphClient.Users[userEmail].GetAsync();
            if (user == null)
                throw new Exception($"User '{userEmail}' not found in Azure AD.");

            await _graphClient.Users[user.Id].PatchAsync(new User
            {
                PasswordProfile = new PasswordProfile
                {
                    Password                     = newPassword,
                    ForceChangePasswordNextSignIn = true
                }
            });
        }
    }
}
