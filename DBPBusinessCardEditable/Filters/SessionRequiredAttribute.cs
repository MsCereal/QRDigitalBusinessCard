using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DBPBusinessCardEditable.Filters;

/// <summary>
/// Action filter that ensures the user has an active login session.
/// Redirects to /login if no session is found.
/// Apply with [SessionRequired] on controllers or individual actions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SessionRequiredAttribute : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var sam = context.HttpContext.Session.GetString("SAM");
        if (string.IsNullOrWhiteSpace(sam))
        {
            context.Result = new RedirectToActionResult("Index", "Login", null);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
