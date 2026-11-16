using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ClientHub.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    ClientHubDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return Unauthorized(new { message = "Email or password is incorrect." });

        var email = NormalizeEmail(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email.ToLower() == email, cancellationToken);
        if (user is null || !user.IsActive ||
            (user.LockedUntil.HasValue && user.LockedUntil > DateTime.UtcNow))
            return Unauthorized(new { message = "Email or password is incorrect." });

        var hasher = new PasswordHasher<User>();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(15);
                user.FailedLoginAttempts = 0;
            }
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { message = "Email or password is incorrect." });
        }

        return await IssueTokenAsync(user, cancellationToken);
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.FullName))
            return BadRequest(new { message = "Name, email, and password are required." });

        var email = NormalizeEmail(request.Email);
        var fullName = request.FullName.Trim();
        if (email.Length < 5 || !email.Contains('@'))
            return BadRequest(new { message = "A valid email is required." });
        if (request.Password.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters long." });

        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email, cancellationToken))
            return Conflict(new { message = "An account with this email already exists." });

        var user = new User
        {
            Email = email,
            FullName = fullName,
            Role = UserRole.Client,
            PasswordHash = string.Empty,
            IsActive = true
        };

        var hasher = new PasswordHasher<User>();
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        return await IssueTokenAsync(user, cancellationToken);
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        id = User.FindFirstValue(ClaimTypes.NameIdentifier),
        name = User.Identity?.Name,
        role = User.FindFirstValue(ClaimTypes.Role)
    });

    private async Task<IActionResult> IssueTokenAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        var key = configuration["Jwt:SigningKey"] ??
            (environment.IsDevelopment() ? "local-development-signing-key-change-me-32chars" : null);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("JWT signing is not configured.");

        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        if (user.ClientId.HasValue) claims.Add(new Claim("client_id", user.ClientId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "ClientHub",
            audience: configuration["Jwt:Audience"] ?? "ClientHub",
            claims: claims,
            notBefore: now,
            expires: now.AddHours(2),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));

        return Ok(new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), user.FullName, user.Role.ToString()));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public sealed record LoginRequest(string Email, string Password);
    public sealed record RegisterRequest(string Email, string Password, string FullName);
    public sealed record AuthResponse(string AccessToken, string Name, string Role);
}
