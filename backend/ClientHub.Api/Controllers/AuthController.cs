using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using ClientHub.Api.Services;
using ClientHub.Application;
using ClientHub.Domain;
using ClientHub.Infrastructure;
using FluentValidation;
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
    IWebHostEnvironment environment,
    IEmailSender emailSender,
    ILogger<AuthController> logger,
    IValidator<AuthController.RegisterRequest> registerValidator,
    IValidator<AuthController.ChangePasswordRequest> changePasswordValidator,
    IValidator<AuthController.UpdateProfileRequest> updateProfileValidator) : ControllerBase
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    [HttpPost("login")]
    [AllowAnonymous]
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
        if (user.ClientId.HasValue &&
            !await db.Clients.AnyAsync(x => x.Id == user.ClientId && x.IsActive, cancellationToken))
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

        return Ok(await IssueTokenAsync(user, cancellationToken));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Auth:AllowSelfRegistration"))
            return NotFound(new { message = "Self-registration is disabled." });
        var validation = await registerValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var email = NormalizeEmail(request.Email);
        var fullName = request.FullName.Trim();
        if (!IsValidEmail(email))
            return BadRequest(new { message = "A valid email is required." });
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
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        db.Users.Add(user);
        return Ok(await IssueTokenAsync(user, cancellationToken));
    }

    [HttpGet("invitations/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ValidateInvitation(string token, CancellationToken cancellationToken)
    {
        var invitation = await FindValidInvitationAsync(token, cancellationToken);
        if (invitation is null) return NotFound(new { message = "This invitation is invalid or has expired." });
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitation.ClientId, cancellationToken);
        if (client is null) return NotFound(new { message = "This invitation is invalid or has expired." });
        return Ok(new { invitation.Email, invitation.FullName, clientName = client.CompanyName, invitation.ExpiresAt });
    }

    [HttpPost("invitations/{token}/accept")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> AcceptInvitation(string token, AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters long." });

        var invitation = await FindValidInvitationAsync(token, cancellationToken);
        if (invitation is null) return NotFound(new { message = "This invitation is invalid or has expired." });
        if (!await db.Clients.AnyAsync(x => x.Id == invitation.ClientId && x.IsActive, cancellationToken))
            return NotFound(new { message = "This invitation is invalid or has expired." });
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == invitation.Email.ToLower(), cancellationToken))
            return Conflict(new { message = "An account with this email already exists." });

        var user = new User
        {
            Email = NormalizeEmail(invitation.Email),
            FullName = invitation.FullName,
            ClientId = invitation.ClientId,
            Role = UserRole.Client,
            ClientRole = await db.Users.AnyAsync(existing => existing.ClientId == invitation.ClientId, cancellationToken)
                ? ClientRole.Member
                : ClientRole.Owner,
            PasswordHash = string.Empty,
            IsActive = true
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        invitation.AcceptedAt = DateTime.UtcNow;
        db.Users.Add(user);
        return Ok(await IssueTokenAsync(user, cancellationToken));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return Unauthorized();
        var now = DateTime.UtcNow;
        var tokenHash = TokenSecurity.HashToken(request.RefreshToken);
        var storedToken = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (storedToken is null) return Unauthorized();

        if (storedToken.RevokedAt.HasValue)
        {
            await RevokeAllRefreshTokensAsync(storedToken.UserId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { message = "Refresh token reuse detected. Please sign in again." });
        }
        if (storedToken.ExpiresAt <= now)
        {
            storedToken.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized();
        }

        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == storedToken.UserId && x.IsActive, cancellationToken);
        if (user is null || (user.ClientId.HasValue &&
            !await db.Clients.AnyAsync(x => x.Id == user.ClientId && x.IsActive, cancellationToken)))
        {
            storedToken.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized();
        }

        var replacement = TokenSecurity.CreateToken();
        var replacementHash = TokenSecurity.HashToken(replacement);
        storedToken.RevokedAt = now;
        storedToken.ReplacedByTokenHash = replacementHash;
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = replacementHash,
            ExpiresAt = now.Add(RefreshTokenLifetime)
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(CreateAuthResponse(user, replacement, now));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            var tokenHash = TokenSecurity.HashToken(request.RefreshToken);
            var token = await db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
            if (token is not null && !token.RevokedAt.HasValue)
            {
                token.RevokedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        await RevokeAllRefreshTokensAsync(user.Id, DateTime.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        var validation = await changePasswordValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var hasher = new PasswordHasher<User>();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            return BadRequest(new { message = "Current password is incorrect." });

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        var now = DateTime.UtcNow;
        await RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);
        return Ok(await IssueTokenAsync(user, cancellationToken));
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        const string responseMessage = "If an account exists for that email, password reset instructions will be sent.";
        var email = NormalizeEmail(request.Email ?? string.Empty);
        if (IsValidEmail(email))
        {
            var user = await db.Users.SingleOrDefaultAsync(x => x.Email.ToLower() == email && x.IsActive, cancellationToken);
            if (user is not null)
            {
                var now = DateTime.UtcNow;
                var previousTokens = await db.PasswordResetTokens
                    .Where(x => x.UserId == user.Id && !x.UsedAt.HasValue).ToListAsync(cancellationToken);
                foreach (var previous in previousTokens) previous.UsedAt = now;
                var resetToken = TokenSecurity.CreateToken();
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    UserId = user.Id,
                    TokenHash = TokenSecurity.HashToken(resetToken),
                    ExpiresAt = now.AddHours(1)
                });
                await db.SaveChangesAsync(cancellationToken);
                var resetUrl = $"{GetFrontendBaseUrl()}/reset-password/{Uri.EscapeDataString(resetToken)}";
                try
                {
                    await emailSender.SendAsync(user.Email, "Reset your Clientry password",
                        $"<p>Use this link to reset your password. It expires in one hour:</p><p><a href=\"{WebUtility.HtmlEncode(resetUrl)}\">Reset password</a></p>",
                        null, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Unable to send a password reset email to {Email}", user.Email);
                }
            }
        }
        return Ok(new { message = responseMessage });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            return BadRequest(new { message = "A valid reset token and password of at least 8 characters are required." });
        var tokenHash = TokenSecurity.HashToken(request.Token);
        var resetToken = await db.PasswordResetTokens.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        var now = DateTime.UtcNow;
        if (resetToken is null || resetToken.UsedAt.HasValue || resetToken.ExpiresAt <= now)
            return BadRequest(new { message = "This password reset link is invalid or has expired." });
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == resetToken.UserId && x.IsActive, cancellationToken);
        if (user is null) return BadRequest(new { message = "This password reset link is invalid or has expired." });

        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        resetToken.UsedAt = now;
        await RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var validation = await updateProfileValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var fullName = request.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(fullName)) return BadRequest(new { message = "Name is required." });
        var user = await GetCurrentUserAsync(cancellationToken);
        if (user is null) return Unauthorized();
        user.FullName = fullName;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { user.Id, user.FullName, user.Email, role = user.Role.ToString(), clientRole = user.ClientRole.ToString() });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var clientId = Guid.TryParse(User.FindFirstValue("client_id"), out var parsedClientId)
            ? parsedClientId
            : (Guid?)null;
        var clientName = clientId.HasValue
            ? await db.Clients.Where(x => x.Id == clientId.Value).Select(x => x.CompanyName).SingleOrDefaultAsync(cancellationToken)
            : null;
        var settings = await db.WorkspaceSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return Ok(new
        {
            id = User.FindFirstValue(ClaimTypes.NameIdentifier),
            name = user?.FullName ?? User.Identity?.Name,
            role = User.FindFirstValue(ClaimTypes.Role),
            clientRole = user?.ClientRole.ToString(),
            workspaceName = clientName ?? "Client workspace",
            supportEmail = settings?.SupportEmail ?? "support@clientry.example"
        });
    }

    private async Task<User?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            : null;
    }

    private async Task<Invitation?> FindValidInvitationAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var tokenHash = TokenSecurity.HashToken(token);
        return await db.Invitations.SingleOrDefaultAsync(x =>
            x.TokenHash == tokenHash && !x.AcceptedAt.HasValue && !x.RevokedAt.HasValue &&
            x.ExpiresAt > DateTime.UtcNow, cancellationToken);
    }

    private async Task RevokeAllRefreshTokensAsync(Guid userId, DateTime revokedAt, CancellationToken cancellationToken)
    {
        var tokens = await db.RefreshTokens.Where(x => x.UserId == userId && !x.RevokedAt.HasValue)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens) token.RevokedAt = revokedAt;
    }

    private async Task<AuthResponse> IssueTokenAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        var refreshToken = TokenSecurity.CreateToken();
        var now = DateTime.UtcNow;
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenSecurity.HashToken(refreshToken),
            ExpiresAt = now.Add(RefreshTokenLifetime)
        });
        await db.SaveChangesAsync(cancellationToken);
        return CreateAuthResponse(user, refreshToken, now);
    }

    private AuthResponse CreateAuthResponse(User user, string refreshToken, DateTime now)
    {
        var key = configuration["Jwt:SigningKey"] ??
            (environment.IsDevelopment() ? "local-development-signing-key-change-me-32chars" : null);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("JWT signing is not configured.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        if (user.ClientId.HasValue) claims.Add(new Claim("client_id", user.ClientId.Value.ToString()));
        var expiresAt = now.Add(AccessTokenLifetime);
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "ClientHub",
            audience: configuration["Jwt:Audience"] ?? "ClientHub",
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), refreshToken,
            expiresAt, user.FullName, user.Role.ToString());
    }

    private string GetFrontendBaseUrl() =>
        (configuration["Frontend:BaseUrl"] ?? "http://localhost:5173").TrimEnd('/');

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static bool IsValidEmail(string email) => email.Length <= 254 && email.Contains('@') &&
        System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
        string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);

    public sealed record LoginRequest(string Email, string Password);
    public sealed record RegisterRequest(string Email, string Password, string FullName);
    public sealed record AcceptInvitationRequest(string Password);
    public sealed record RefreshRequest(string RefreshToken);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record ForgotPasswordRequest(string? Email);
    public sealed record ResetPasswordRequest(string Token, string Password);
    public sealed record UpdateProfileRequest(string FullName);
    public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAtUtc, string Name, string Role);
}
