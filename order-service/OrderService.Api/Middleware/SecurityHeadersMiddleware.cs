namespace OrderService.Api.Middleware;

/// <summary>
/// Middleware to add security headers to all HTTP responses
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // If the request targets the Swagger UI or its static assets, skip adding strict headers
        // This avoids blocking the Swagger UI's scripts/styles/fetches by CSP while keeping the
        // strict headers in place for the rest of the application.
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger-ui", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("swagger-ui-bundle.js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("swagger-ui-standalone-preset.js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("swagger-ui.css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("favicon-32x32.png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("favicon-16x16.png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/swagger.json", StringComparison.OrdinalIgnoreCase)
            )
        {
            await _next(context);
            return;
        }

        // HTTP Strict Transport Security (HSTS)
        // Tells browsers to only use HTTPS for future requests
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");

        // X-Content-Type-Options
        // Prevents MIME type sniffing
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        // X-Frame-Options
        // Prevents clickjacking attacks
        context.Response.Headers.Append("X-Frame-Options", "DENY");

        // X-XSS-Protection
        // Enables browser's XSS protection
        context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");

        // Content-Security-Policy
        // Mitigates XSS and data injection attacks
        context.Response.Headers.Append("Content-Security-Policy", 
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'");

        // Referrer-Policy
        // Controls how much referrer information is sent
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

        // Permissions-Policy (formerly Feature-Policy)
        // Disables unnecessary browser features
        context.Response.Headers.Append("Permissions-Policy", 
            "geolocation=(), " +
            "microphone=(), " +
            "camera=(), " +
            "payment=(), " +
            "usb=(), " +
            "magnetometer=()");

        // Remove server header to avoid information disclosure
        context.Response.Headers.Remove("Server");
        context.Response.Headers.Remove("X-Powered-By");

        await _next(context);
    }
}

/// <summary>
/// Extension method to add security headers middleware
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
