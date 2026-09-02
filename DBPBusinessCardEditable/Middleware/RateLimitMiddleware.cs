using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DBPBusinessCardEditable.Middleware
{
    /// <summary>
    /// Simple sliding-window rate limiter for /api/* routes.
    /// Default: 60 requests per minute per IP.
    /// </summary>
    public class RateLimitMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly int _maxRequests;
        private readonly TimeSpan _window;

        // IP → (window start, count)
        private static readonly ConcurrentDictionary<string, (DateTime Start, int Count)> _counters
            = new ConcurrentDictionary<string, (DateTime, int)>();

        public RateLimitMiddleware(RequestDelegate next, int maxRequests = 60, int windowSeconds = 60)
        {
            _next        = next;
            _maxRequests = maxRequests;
            _window      = TimeSpan.FromSeconds(windowSeconds);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Only rate-limit API routes
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await _next(context);
                return;
            }

            string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTime.UtcNow;

            var entry = _counters.AddOrUpdate(ip,
                _ => (now, 1),
                (_, old) =>
                {
                    if (now - old.Start > _window) return (now, 1);
                    return (old.Start, old.Count + 1);
                });

            context.Response.Headers["X-RateLimit-Limit"]     = _maxRequests.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _maxRequests - entry.Count).ToString();

            if (entry.Count > _maxRequests)
            {
                context.Response.StatusCode  = 429;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Too many requests. Please slow down.\"}");
                return;
            }

            await _next(context);
        }
    }
}
