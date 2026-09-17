using BookHiveLibrary.Data;
using BookHiveLibrary.Helpers;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BookHiveLibrary.Services
{
    // This background service runs automatically while the app is running.
    // It wakes up every hour and checks three things:
    //
    //   1. Pending reservations past their pickup deadline → void them and notify the student
    //   2. Books due in less than 12 hours → send a reminder to the student
    //   3. Books that are now past their due date → mark as Overdue and notify the adviser
    //
    // Because it runs in the background, it uses a fresh database scope each time
    // (background services don't share the normal HTTP request scope).
    public class ReminderBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<ReminderBackgroundService> _logger;
        private readonly IHubContext<LibraryHub> _hub;

        // How often the reminder check should run
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

        // How many hours before the due date we send the "almost due" reminder
        private const int ReminderHoursAhead = 12;

        public ReminderBackgroundService(IServiceProvider services, ILogger<ReminderBackgroundService> logger, IHubContext<LibraryHub> hub)
        {
            _services = services;
            _logger   = logger;
            _hub      = hub;
        }

        // This method runs in a loop until the app shuts down.
        // It waits for the check interval, then processes reminders.
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Reminder background service started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessRemindersAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while processing reminders.");
                }

                // Wait one hour before checking again
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }

        // The main reminder logic.
        // Creates a fresh database scope (so we get a fresh DbContext) and:
        //   Step 1 — find PickedUp books due within the next 12 hours → send reminder
        //   Step 2 — find PickedUp books already past their due date → mark Overdue and notify adviser
        private async Task ProcessRemindersAsync()
        {
            // Create a fresh scope so we get a fresh ApplicationDbContext
            using var scope   = _services.CreateScope();
            var context       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var emailService  = scope.ServiceProvider.GetRequiredService<EmailService>();
            var smsService    = scope.ServiceProvider.GetRequiredService<SmsService>();

            // DueDate is stored as UTC (see BookReservation.DueDate), so comparisons
            // here use UtcNow. DateTime.Now happened to equal this by coincidence on
            // Azure (its OS clock is UTC) — the same code would have been quietly
            // wrong on a server set to any other timezone.
            DateTime now                  = DateTime.UtcNow;
            DateTime reminderCutoff       = now.AddHours(ReminderHoursAhead); // 12 hours from now

            // ── Step 0: Void expired Pending reservations ─────────────────────
            //
            // A reservation that isn't picked up within its window used to only get
            // voided when a librarian happened to open the Borrow/Index page — a
            // student could sit on "Pending" indefinitely otherwise. Running it here
            // too means it reliably clears (and the student gets notified) within an
            // hour of expiring, not whenever someone next opens that page.
            try
            {
                int voidedCount = await ReservationExpiryService.VoidExpiredPendingReservationsAsync(context, _hub);
                if (voidedCount > 0)
                    _logger.LogInformation("Auto-voided {Count} expired reservation(s).", voidedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-void expired reservations.");
            }

            // ── Step 1: Send "almost due" reminders ───────────────────────────
            //
            // Find books that:
            //   - Are currently borrowed (status = PickedUp)
            //   - Are due within the next 12 hours (but not yet past due)
            //   - Haven't been sent a reminder yet (ReminderSent = false)

            var dueSoonReservations = await context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation =>
                    reservation.Status == "PickedUp"    &&
                    reservation.DueDate <= reminderCutoff &&
                    reservation.DueDate > now            &&
                    !reservation.ReminderSent)
                .ToListAsync();

            foreach (var reservation in dueSoonReservations)
            {
                try
                {
                    // DueDate is stored as UTC — convert to PH time before it goes into
                    // an email/SMS a student actually reads, or the due time quoted
                    // back to them is off by 8 hours.
                    DateTime dueDatePh = PhTime.FromUtc(reservation.DueDate!.Value); // DueDate is DateTime? — .Value is safe here because we filtered for non-null due dates

                    // Send an email reminder to the student
                    await emailService.SendReturnReminderAsync(
                        reservation.User!.Email!,
                        reservation.User.FirstName + " " + reservation.User.LastName,
                        reservation.Book!.Title,
                        dueDatePh);

                    // Also send an SMS if the student has a phone number
                    bool studentHasPhone = !string.IsNullOrEmpty(reservation.User.PhoneNumber);
                    if (studentHasPhone)
                    {
                        await smsService.SendReturnReminderAsync(
                            reservation.User.PhoneNumber!,
                            reservation.User.FirstName + " " + reservation.User.LastName,
                            reservation.Book.Title,
                            dueDatePh);
                    }

                    // Mark the reminder as sent so we don't send it again
                    reservation.ReminderSent = true;
                    _logger.LogInformation(
                        "Sent reminder to {Email} for book '{Title}' due on {DueDate}.",
                        reservation.User.Email, reservation.Book.Title, reservation.DueDate);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send reminder for reservation {ReservationId}.", reservation.Id);
                }
            }

            await context.SaveChangesAsync();

            // ── Step 2: Mark overdue books and notify the adviser ─────────────
            //
            // Find books that:
            //   - Are currently borrowed (status = PickedUp)
            //   - Are past their due date (due date is in the past)
            // These haven't been marked overdue yet (still showing as PickedUp).

            var overdueReservations = await context.BookReservations
                .Include(reservation => reservation.User)
                .Include(reservation => reservation.Book)
                .Where(reservation =>
                    reservation.Status == "PickedUp" &&
                    reservation.DueDate < now)
                .ToListAsync();

            foreach (var reservation in overdueReservations)
            {
                // Update the status so the librarian's dashboard shows it as overdue
                reservation.Status = "Overdue";

                try
                {
                    // Notify the class adviser about the overdue book
                    bool adviserEmailAvailable = !string.IsNullOrEmpty(reservation.User?.AdviserEmail);
                    if (adviserEmailAvailable)
                    {
                        await emailService.SendOverdueAdviserNotificationAsync(
                            reservation.User!.AdviserEmail!,
                            reservation.User.FirstName + " " + reservation.User.LastName,
                            reservation.Book!.Title,
                            PhTime.FromUtc(reservation.DueDate!.Value));
                    }

                    _logger.LogInformation(
                        "Marked overdue and notified adviser for reservation {ReservationId}.",
                        reservation.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to notify adviser for reservation {ReservationId}.", reservation.Id);
                }
            }

            await context.SaveChangesAsync();
        }
    }
}
