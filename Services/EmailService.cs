using System.Net;
using System.Net.Mail;

namespace BookHiveLibrary.Services
{
    // This service sends all outgoing emails from BookHive using the school's Office 365 email.
    //
    // It sends:
    //   - A reminder email to the student when a borrowed book is almost due
    //   - An overdue notification to the class adviser when a book is not returned on time
    //   - An OTP (one-time password) code for account verification
    //
    // Email settings (sender address, password) are stored in appsettings.json under "EmailSettings".
    public class EmailService
    {
        private readonly IConfiguration _configuration;

        public EmailService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // Sends a return reminder to the student before their book's due date.
        // Called by the background service when a book is due within 12 hours.
        public async Task SendReturnReminderAsync(
            string recipientEmail,
            string studentName,
            string bookTitle,
            DateTime dueDate)
        {
            string subject = "BookHive – Book Return Reminder";

            string body = $@"
                <p>Hi <strong>{studentName}</strong>,</p>
                <p>This is a reminder that your borrowed book is due soon:</p>
                <ul>
                    <li><strong>Book:</strong> {bookTitle}</li>
                    <li><strong>Due Date:</strong> {dueDate:MMMM d, yyyy h:mm tt}</li>
                </ul>
                <p>Please return it to the library on time to avoid penalties.</p>
                <br/>
                <p>– BookHive Library System</p>";

            await SendAsync(recipientEmail, subject, body);
        }

        // Sends an overdue notification to the student's class adviser.
        // Called when a book is past its due date and still not returned.
        public async Task SendOverdueAdviserNotificationAsync(
            string adviserEmail,
            string studentName,
            string bookTitle,
            DateTime dueDate)
        {
            string subject = "BookHive – Overdue Book Notification";

            string body = $@"
                <p>Dear Adviser,</p>
                <p>One of your students has an overdue library book:</p>
                <ul>
                    <li><strong>Student:</strong> {studentName}</li>
                    <li><strong>Book:</strong> {bookTitle}</li>
                    <li><strong>Due Date:</strong> {dueDate:MMMM d, yyyy h:mm tt}</li>
                </ul>
                <p>Please remind the student to return the book as soon as possible.</p>
                <br/>
                <p>– BookHive Library System</p>";

            await SendAsync(adviserEmail, subject, body);
        }

        // Sends a one-time password (OTP) code to the user's email.
        // Used during the email verification step of the login flow.
        public async Task SendOtpAsync(string recipientEmail, string otpCode)
        {
            string subject = "BookHive – Your Verification Code";

            string body = $@"
                <p>Your BookHive verification code is:</p>
                <h2 style='letter-spacing: 6px;'>{otpCode}</h2>
                <p>This code expires in 5 minutes.</p>
                <p>If you did not request this, please ignore this email.</p>
                <br/>
                <p>– BookHive Library System</p>";

            await SendAsync(recipientEmail, subject, body);
        }

        // Internal helper that actually sends the email using the Office 365 SMTP server.
        // All public methods above call this to do the actual sending.
        private async Task SendAsync(string recipientEmail, string subject, string body)
        {
            // Read email settings from appsettings.json
            string senderName  = _configuration["EmailSettings:SenderName"]  ?? "BookHive";
            string senderEmail = _configuration["EmailSettings:SenderEmail"]  ?? "";
            string appPassword = _configuration["EmailSettings:AppPassword"]  ?? "";

            var mailMessage = new MailMessage
            {
                From       = new MailAddress(senderEmail, senderName),
                Subject    = subject,
                Body       = body,
                IsBodyHtml = true  // The body above uses HTML tags
            };
            mailMessage.To.Add(recipientEmail);

            // Office 365 requires SMTP with StartTLS on port 587
            using var smtpClient = new SmtpClient("smtp.office365.com", 587)
            {
                Credentials  = new NetworkCredential(senderEmail, appPassword),
                EnableSsl    = true,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            await smtpClient.SendMailAsync(mailMessage);
        }
    }
}
