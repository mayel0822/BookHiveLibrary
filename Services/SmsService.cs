using System.Text;
using System.Text.Json;

namespace BookHiveLibrary.Services
{
    public class SmsService
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private const string SemaphoreApiUrl = "https://api.semaphore.co/api/v4/messages";

        public SmsService(IConfiguration configuration, HttpClient httpClient)
        {
            _configuration = configuration;
            _httpClient = httpClient;
        }

        public async Task SendOtpAsync(string phoneNumber, string otp)
        {
            var message = $"Your BookHive verification code is: {otp}. This code expires in 5 minutes.";
            await SendAsync(phoneNumber, message);
        }

        public async Task SendReturnReminderAsync(string phoneNumber, string bookTitle, DateTime dueDate)
        {
            var message = $"BookHive Reminder: Your borrowed book \"{bookTitle}\" is due on {dueDate:MMM dd, yyyy hh:mm tt}. Please return it on time.";
            await SendAsync(phoneNumber, message);
        }

        private static string NormalizePhone(string phoneNumber)
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("63")) return digits;
            if (digits.StartsWith("0")) return "63" + digits[1..];
            return "63" + digits;
        }

        private async Task SendAsync(string phoneNumber, string message)
        {
            phoneNumber = NormalizePhone(phoneNumber);
            var apiKey = _configuration["Semaphore:ApiKey"];
            var senderName = _configuration["Semaphore:SenderName"] ?? "BookHive";

            var payload = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("apikey", apiKey!),
                new KeyValuePair<string, string>("number", phoneNumber),
                new KeyValuePair<string, string>("message", message),
                new KeyValuePair<string, string>("sendername", senderName)
            });

            var response = await _httpClient.PostAsync(SemaphoreApiUrl, payload);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception($"Semaphore SMS failed ({response.StatusCode}): {body}");
            }
        }
    }
}
