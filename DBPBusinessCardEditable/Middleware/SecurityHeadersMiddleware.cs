namespace DBPBusinessCardEditable.Middleware;

/// <summary>
/// Adds OWASP-recommended security headers + Content-Security-Policy to every response.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Prevent clickjacking
        headers["X-Frame-Options"] = "DENY";

        // Prevent MIME sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // No referrer leakage
        headers["Referrer-Policy"] = "no-referrer";

        // Only allow features this app actually uses
        headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=()";

        // HSTS — only set over HTTPS to avoid breaking HTTP dev
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        // Content-Security-Policy
        // script-src 'self' allows our own JS; unsafe-inline needed for inline <script> in Razor views.
        // For the final production build, replace unsafe-inline with a nonce per request.
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
            "font-src 'self' https://cdnjs.cloudflare.com; " +
            "img-src 'self' data:; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self';";

        await next(context);
    }
}
