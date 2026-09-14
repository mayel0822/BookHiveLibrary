using BookHiveLibrary.Data;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using BookHiveLibrary.Hubs;
using BookHiveLibrary.Models;
using BookHiveLibrary.Seeders;
using BookHiveLibrary.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Proxy / HTTPS forwarding ──────────────────────────────────────────────────
// Required for Azure App Service — allows the app to see the real client IP
// and treat the request as HTTPS even behind the Azure load balancer
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ── Database ──────────────────────────────────────────────────────────────────
// Connects to Azure SQL (or local SQL Server in dev) using the connection string in appsettings.json
// EnableRetryOnFailure handles Azure SQL auto-pause reconnections (up to 5 retries, 10s apart)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null)));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ── ASP.NET Identity ──────────────────────────────────────────────────────────
// Manages local user accounts, password hashing (PBKDF2), and session cookies.
// RequireConfirmedAccount = false because email verification is handled via our custom OTP flow.
// Lockout: 5 failed attempts = 1-hour lockout for security.
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;

    // Lock account after 5 failed attempts for 1 hour
    options.Lockout.AllowedForNewUsers      = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromHours(1);
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// ── Microsoft OAuth (Azure AD SSO) ───────────────────────────────────────────
// Students and Professors log in via their Microsoft school accounts.
// ClientSecret must never be committed to GitHub — keep it blank in source, restore locally.
// TenantId restricts login to this school's Azure AD tenant only.
builder.Services.AddAuthentication()
    .AddMicrosoftAccount(options =>
    {
        options.ClientId              = builder.Configuration["AzureAd:ClientId"]!;
        options.ClientSecret          = builder.Configuration["AzureAd:ClientSecret"]!;
        options.AuthorizationEndpoint = $"https://login.microsoftonline.com/{builder.Configuration["AzureAd:TenantId"]}/oauth2/v2.0/authorize";
        options.TokenEndpoint         = $"https://login.microsoftonline.com/{builder.Configuration["AzureAd:TenantId"]}/oauth2/v2.0/token";
    });

// ── Cookie security settings ──────────────────────────────────────────────────
// SameSiteMode.None + Secure = required for OAuth redirects to work across domains
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.None;
    options.Secure   = CookieSecurePolicy.Always;
    options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
});

// ── MVC + SignalR ─────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSignalR(); // Used for real-time RFID tap notifications and librarian kiosk updates

// ── Memory Cache (used by the error rate limiter) ─────────────────────────────
// Stores per-IP error counts and block flags in server memory.
// No database needed — counts reset automatically when the app restarts.
builder.Services.AddMemoryCache();

// ── Custom services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<EmailService>();                                          // Office 365 SMTP for OTP, reminders, overdue notices
builder.Services.AddHttpClient<SmsService>();                                        // Semaphore SMS API for phone OTP
builder.Services.AddScoped<GraphService>();                                          // Microsoft Graph API for Azure AD password resets
builder.Services.AddHostedService<BookHiveLibrary.Services.ReminderBackgroundService>(); // Background service that checks for due/overdue books daily

// ── Timezone ──────────────────────────────────────────────────────────────────
// Force Philippine Standard Time so DateTime.Now returns PH time consistently on Azure
Environment.SetEnvironmentVariable("TZ", "Asia/Manila");

var app = builder.Build();

// ── Error handling ────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts(); // Adds Strict-Transport-Security header for HTTPS enforcement
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
// ORDER MATTERS — do not rearrange these
app.UseForwardedHeaders();    // Must be first — reads X-Forwarded-For / X-Forwarded-Proto from Azure
app.UseHttpsRedirection();
app.UseStaticFiles();         // Serves wwwroot (CSS, JS, images)
app.UseCookiePolicy();
app.UseRouting();
app.UseAuthentication();      // Reads the login cookie / Microsoft OAuth token
app.UseAuthorization();       // Checks [Authorize] attributes on controllers

// ── Error Rate Limiter (security guard) ───────────────────────────────────────
// Counts 400/401/403/404/405/500 errors per IP address.
// Blocks the IP temporarily if they hit too many errors too quickly.
// Must run AFTER authentication so we know who the user is when logging.
app.UseMiddleware<BookHiveLibrary.Middleware.ErrorRateLimiterMiddleware>();

// ── Deactivated account guard ─────────────────────────────────────────────────
// Runs on every request: if the logged-in user's IsActive = false, sign them out immediately.
// This ensures MIS can deactivate an account and the user is kicked out on their next page load.
app.Use(async (httpContext, next) =>
{
    bool userIsLoggedIn = httpContext.User.Identity?.IsAuthenticated == true;
    if (userIsLoggedIn)
    {
        var userManager   = httpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = httpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(httpContext.User);

        bool accountIsDeactivated = user != null && !user.IsActive;
        if (accountIsDeactivated)
        {
            await signInManager.SignOutAsync();
            httpContext.Response.Redirect("/Account/Login");
            return; // Stop processing this request — the user is no longer allowed in
        }
    }
    await next();
});

// ── Routes ────────────────────────────────────────────────────────────────────
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();
app.MapHub<LibraryHub>("/hubs/library"); // SignalR hub endpoint for real-time library events

// ── Startup: migrations + seeding ────────────────────────────────────────────
// Runs once on app startup — applies any pending EF Core migrations and seeds
// default roles (Student, Professor, Librarian, MIS) and the initial admin user
using (var scope = app.Services.CreateScope())
{
    var services    = scope.ServiceProvider;
    var db          = services.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    await RoleSeeder.SeedRolesAsync(roleManager);
    await UserSeeder.SeedUsersAsync(userManager);
}

app.Run();
