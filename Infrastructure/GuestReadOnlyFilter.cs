using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// A guest may look, never touch. Enforced here, once, for every endpoint rather than in each
/// controller: anything that is not a plain read is refused before the action runs, so an
/// endpoint added later is safe by default instead of safe only if somebody remembered.
/// </summary>
public class GuestReadOnlyFilter : IAsyncAuthorizationFilter
{
    private static readonly string[] ReadMethods = ["GET", "HEAD", "OPTIONS"];

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.IsGuest()
            && !ReadMethods.Contains(context.HttpContext.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Guests can only look around.",
                Detail = "Sign in with your QaDoc account to make changes."
            })
            { StatusCode = StatusCodes.Status403Forbidden };
        }
        return Task.CompletedTask;
    }
}
