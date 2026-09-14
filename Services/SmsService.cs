namespace BookHiveLibrary.Services
{
    // This service sends SMS messages using the Semaphore SMS API.
    // Semaphore is a Philippine SMS provider — it delivers text messages locally.
    //
    // It sends:
    //   - OTP codes (for phone verification during login)
    //   - Book return reminders (when a borrowed book is almost due)
    //
    // API key and sender name are stored in appsettings.json under "Semaphore".
    public class SmsService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        // The Semaphore API endpoint for sending messages
        private const string SemaphoreApiUrl = "https://api.semaphore.co/api/v4/messages";

        public SmsService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient    = httpClient;
            _configuration = configuration;
        }

        // Sends a one-time password code to the student's phone number.
        // Used during login to verify the student owns their registered phone number.
        public async Task SendOtpAsync(string phoneNumber, string otpCode)
        {
            string message = $"Your BookHive verification code is: {otpCode}. This code expires in 5 minutes.";
            await SendAsync(phoneNumber, message);
        }

        // Sends a "your book is almost due" SMS reminder to the student.
        // Called by the background service when a book is due within 12 hours.
        public async Task SendReturnReminderAsync(
            string phoneNumber,
            string studentName,
            string bookTitle,
            DateTime dueDate)
        {
            string formattedDueDate = dueDate.ToString("MMM d, yyyy h:mm tt");
            string message = $"Hi {studentName}, reminder from BookHive: '{bookTitle}' is due on {formattedDueDate}. Please return it on time.";
            await SendAsync(phoneNumber, message);
        }

        // Converts a Philippine phone number to the international format Semaphore expects.
        // Examples:
        //   09171234567  → 639171234567
        //   +639171234567 → 639171234567
        //   639171234567  → 639171234567 (already correct)
        private static string NormalizePhoneNumber(string rawPhoneNumber)
        {
            string digits = rawPhoneNumber.Trim();

            if (digits.StartsWith("+63"))
                return "63" + digits.Substring(3); // Remove the "+" prefix

            if (digits.StartsWith("09"))
                return "63" + digits.Substring(1); // Replace the leading "0" with "63"

            return digits; // Already in international format
        }

        // Internal helper that sends a POST request to the Semaphore API.
        // All public methods above call this to do the actual sending.
        private async Task SendAsync(string phoneNumber, string messageText)
        {
            string apiKey      = _configuration["Semaphore:ApiKey"]     ?? "";
            string senderName  = _configuration["Semaphore:SenderName"] ?? "BOOKHIVE";

            string normalizedPhone = NormalizePhoneNumber(phoneNumber);

            // Semaphore expects form data (not JSON)
            var formFields = new Dictionary<string, string>
            {
                ["apikey"]      = apiKey,
                ["number"]      = normalizedPhone,
                ["message"]     = messageText,
                ["sendername"]  = senderName
            };

            var formContent = new FormUrlEncodedContent(formFields);

            var response = await _httpClient.PostAsync(SemaphoreApiUrl, formContent);

            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Semaphore API error: {response.StatusCode} – {responseBody}");
            }
        }
    }
}
