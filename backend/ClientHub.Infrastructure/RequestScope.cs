using System.Security.Claims;
using ClientHub.Application;
using Microsoft.AspNetCore.Http;

namespace ClientHub.Infrastructure;

public sealed class RequestScope(IHttpContextAccessor accessor) : IRequestScope
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? ClientId => Guid.TryParse(Principal?.FindFirstValue("client_id"), out var id) ? id : null;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public bool IsClient => Principal?.IsInRole("Client") == true;
}
