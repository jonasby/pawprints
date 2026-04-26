using System.Security.Claims;

namespace PawPrints.Api;

public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor)
{
    public string Email =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
        ?? throw new InvalidOperationException("The current user has no email claim.");

    public string Subject =>
        httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? Email;
}
