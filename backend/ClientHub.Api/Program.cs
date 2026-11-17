using System.Text;
using System.Threading.RateLimiting;
using ClientHub.Api.Hubs;
using ClientHub.Api.Services;
using ClientHub.Application;
using ClientHub.Infrastructure;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestScope, RequestScope>();
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<InvoicePdfService>();
builder.Services.AddDbContext<ClientHubDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ClientHub") ?? "Data Source=clienthub.db"));
builder.Services.AddControllers();
builder.Services.AddValidatorsFromAssemblyContaining<ClientHub.Api.Validators.ProjectRequestValidator>();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 8,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
        options.AddPolicy("project-requests", context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var signingKey = builder.Configuration["Jwt:SigningKey"] ??
    (builder.Environment.IsDevelopment() ? "local-development-signing-key-change-me-32chars" : null);
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "ClientHub",
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ClientHub",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                context.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    try { await next(); }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled request failure");
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
            title: "An unexpected error occurred.").ExecuteAsync(context);
    }
});
app.UseSerilogRequestLogging();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapControllers();
app.MapHub<ProjectHub>("/hubs/projects/{projectId:guid}").RequireAuthorization();

await DemoData.InitializeAsync(app.Services);
app.Run();

public partial class Program;
