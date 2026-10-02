using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DBPBusinessCardEditable.Filters;

/// <summary>
/// Catches antiforgery validation failures and redirects to login
/// instead of showing a raw HTTP 400 page.
/// Registered as both IExceptionFilter (action level) and
/// handled via middleware for pre-action failures.
/// </summary>
public sealed class AntiforgeryExceptionFilter : IExceptionFilter, IFilterMetadata
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not AntiforgeryValidationException)
            return;

        context.ExceptionHandled = true;
        context.Result = new RedirectResult("/login?reason=session_expired");
    }
}
