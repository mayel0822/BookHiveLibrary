using Microsoft.Extensions.Caching.Memory;

namespace BookHiveLibrary.Middleware
{
    // ── Error Rate Limiter Middleware ─────────────────────────────────────────────
    //
    // This middleware acts as a security guard at the front door of the application.
    // It watches every request and counts how many error responses (400, 401, 403,
    // 404, 405, 500) an IP address receives within a time window.
    //
    // If an IP address hits too many errors too quickly, it gets blocked temporarily.
    // This protects against:
    //   - SQL injection probing (repeated 400 errors)
    //   - Role bypass attempts (repeated 403 errors)
    //   - URL scanning / directory traversal (repeated 404 errors)
    //   - Server crash attempts (repeated 500 errors)
    //
    // ── Thresholds ────────────────────────────────────────────────────────────────
    //
    //   🔴 HIGH RISK   — block after 3 hits  → 401, 403
    //   🔴 HIGH RISK   — block after 5 hits  → 400, 500
    //   🟡 MEDIUM RISK — block after 5 hits  → 404
    //   🟢 LOWER RISK  — block after 5 hits  → 405
    //
    //   Time window  : 5 minutes  (errors older than this are forgotten)
    //   Block duration: 15 minutes (how long a blocked IP stays blocked)
    //
    // ── How it works ──────────────────────────────────────────────────────────────
    //
    //   1. Request comes in → check if IP is blocked → if yes, show Access Denied
    //   2. If not blocked → let the request through normally
    //   3. After the response → check the status code
    //   4. If it's a tracked error code → add 1 to that IP's error count
    //   5. If count reaches the threshold for that error type → block the IP
    //
    public class ErrorRateLimiterMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IMemoryCache    _cache;
        private readonly ILogger<ErrorRateLimiterMiddleware> _logger;

        // ── Settings — change these to adjust the security level ─────────────────

        // How many 403 Forbidden hits (role bypass attempts) before blocking
        private const int HighRiskLimit = 10;

        // How many 400/404/405/500 hits before blocking
        private const int MediumRiskLimit = 20;

        // How long to watch for errors (errors outside this window are forgotten)
        private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(5);

        // How long to block a suspicious IP
        private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

        // Cache key prefix so blocked IPs and error counts don't collide
        private const string BlockedKeyPrefix = "blocked_";
        private const string CountKeyPrefix   = "errcount_";

        public ErrorRateLimiterMiddleware(
            RequestDelegate next,
            IMemoryCache cache,
            ILogger<ErrorRateLimiterMiddleware> logger)
        {
            _next   = next;
            _cache  = cache;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext httpContext)
        {
            string clientIp = GetClientIp(httpContext);

            // ── Step 1: Check if this IP is already blocked ───────────────────────
            bool ipIsBlocked = _cache.TryGetValue(BlockedKeyPrefix + clientIp, out _);
            if (ipIsBlocked)
            {
                _logger.LogWarning("[RATE LIMITER] Blocked IP attempted access: {IP} → {Path}",
                    clientIp, httpContext.Request.Path);

                httpContext.Response.StatusCode = 429; // 429 = Too Many Requests
                httpContext.Response.ContentType = "text/html";

                await httpContext.Response.WriteAsync(BuildBlockedPage());
                return; // Stop here — don't let them in
            }

            // ── Step 2: Let the request through normally ──────────────────────────
            await _next(httpContext);

            // ── Step 3: Check what status code came back ──────────────────────────
            int    statusCode   = httpContext.Response.StatusCode;
            string requestPath  = httpContext.Request.Path.Value?.ToLower() ?? "";

            // 401 is intentionally NOT tracked — too many normal flows return it:
            // Microsoft OAuth challenge, RFID device key mismatch, session expiry.
            // Tracking 401 blocks real users on shared school/library networks.
            bool isTrackedError = statusCode is 400 or 403 or 404 or 405 or 500;
            if (!isTrackedError)
                return; // Normal response — nothing to count

            // ── Exclude legitimate paths from being counted ───────────────────────
            // These paths intentionally return 401 / 302 as part of normal flows.
            // Counting them would block real users or hardware devices.
            bool isAuthPath = requestPath.StartsWith("/account/microsoftlogin")        // starts Microsoft OAuth
                           || requestPath.StartsWith("/account/microsoftlogincallback") // OAuth redirect back
                           || requestPath.StartsWith("/signin-microsoft")               // Microsoft's redirect URI
                           || requestPath.StartsWith("/account/login")                  // normal login page
                           || requestPath.StartsWith("/account/adminlogin")             // librarian login page
                           || requestPath.StartsWith("/account/mislogin")               // MIS login page
                           || requestPath.StartsWith("/librarian/rfidtap")              // ESP32 kiosk entrance reader
                           || requestPath.StartsWith("/librarian/desktap");             // ESP32 desk reader

            if (isAuthPath)
                return; // Don't count errors on these paths — they have their own auth logic

            // ── Step 4: Determine the risk level and threshold for this error ─────
            int threshold = statusCode switch
            {
                403 => HighRiskLimit,   // Forbidden            — HIGH (10 hits) — role bypass attempts
                400 => MediumRiskLimit, // Bad Request          — MEDIUM (20 hits) — malformed requests
                500 => MediumRiskLimit, // Internal Server Error— MEDIUM (20 hits) — crash attempts
                404 => MediumRiskLimit, // Not Found            — MEDIUM (20 hits) — URL scanning
                405 => MediumRiskLimit, // Method Not Allowed   — LOWER  (20 hits) — wrong HTTP method
                _   => MediumRiskLimit
            };

            // ── Step 5: Increment the error count for this IP ─────────────────────
            string countKey = CountKeyPrefix + clientIp + "_" + statusCode;

            // Get the current count (or start at 0)
            int currentCount = _cache.TryGetValue(countKey, out int existingCount) ? existingCount : 0;
            int newCount      = currentCount + 1;

            // Save the updated count — it will automatically expire after the window
            _cache.Set(countKey, newCount, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ErrorWindow
            });

            _logger.LogInformation(
                "[RATE LIMITER] {IP} → {Code} error (count: {Count}/{Threshold}) on {Path}",
                clientIp, statusCode, newCount, threshold, httpContext.Request.Path);

            // ── Step 6: Block if they've hit the threshold ────────────────────────
            bool thresholdReached = newCount >= threshold;
            if (thresholdReached)
            {
                // Store the block — expires automatically after BlockDuration
                _cache.Set(BlockedKeyPrefix + clientIp, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = BlockDuration
                });

                _logger.LogWarning(
                    "[RATE LIMITER] ⛔ IP BLOCKED: {IP} — reached {Count} {Code} errors. Blocked for {Minutes} minutes.",
                    clientIp, newCount, statusCode, BlockDuration.TotalMinutes);
            }
        }

        // Gets the real client IP address.
        // Checks the X-Forwarded-For header first (set by Azure's load balancer),
        // then falls back to the direct connection IP.
        private static string GetClientIp(HttpContext httpContext)
        {
            // X-Forwarded-For is set by Azure App Service / reverse proxies
            string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();

            if (!string.IsNullOrEmpty(forwardedFor))
            {
                // The header can contain multiple IPs — the first one is the real client
                string firstIp = forwardedFor.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(firstIp))
                    return firstIp;
            }

            // Fall back to the direct connection IP
            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        // Builds the HTML page shown to blocked IPs.
        // Kept minimal and generic — reveals no system information.
        private static string BuildBlockedPage()
        {
            return @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1.0' />
    <title>Access Denied — BookHive</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            background: #000147;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            color: white;
        }
        .card {
            background: rgba(255,255,255,0.07);
            border: 1px solid rgba(255,255,255,0.12);
            border-radius: 20px;
            padding: 48px 40px;
            text-align: center;
            max-width: 420px;
            width: 90%;
        }
        .icon {
            font-size: 3rem;
            margin-bottom: 20px;
        }
        h1 {
            font-size: 1.5rem;
            font-weight: 700;
            margin-bottom: 12px;
        }
        p {
            font-size: 0.95rem;
            color: rgba(255,255,255,0.65);
            line-height: 1.6;
            margin-bottom: 8px;
        }
        .hint {
            font-size: 0.8rem;
            color: rgba(255,255,255,0.35);
            margin-top: 20px;
        }
        a {
            display: inline-block;
            margin-top: 28px;
            padding: 10px 28px;
            background: rgba(255,255,255,0.12);
            border: 1px solid rgba(255,255,255,0.2);
            border-radius: 10px;
            color: white;
            text-decoration: none;
            font-size: 0.9rem;
            transition: background 0.2s;
        }
        a:hover { background: rgba(255,255,255,0.2); }
    </style>
</head>
<body>
    <div class='card'>
        <div class='icon'>🚫</div>
        <h1>Access Temporarily Blocked</h1>
        <p>Too many suspicious requests were detected from your connection.</p>
        <p>Your access has been temporarily suspended for security reasons.</p>
        <p class='hint'>Please wait 15 minutes before trying again.<br/>If you believe this is a mistake, contact the system administrator.</p>
        <a href='/'>Return to Home</a>
    </div>
</body>
</html>";
        }
    }
}
