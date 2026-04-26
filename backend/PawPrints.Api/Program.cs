using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;
using PawPrints.Api.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<SnapshotSyncService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddDbContext<PawPrintsDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("PawPrintsDb");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseSqlite("Data Source=pawprints-local.db");
        return;
    }

    options.UseSqlServer(connectionString);
});

var googleClientId = ProgramConfiguration.GetFirstConfiguredValue(
    builder.Configuration,
    "Authentication:Google:ClientId",
    "Google:ClientId",
    "GOOGLE_CLIENT_ID",
    "GOOGLE_OAUTH_CLIENT_ID"
);
var googleClientSecret = ProgramConfiguration.GetFirstConfiguredValue(
    builder.Configuration,
    "Authentication:Google:ClientSecret",
    "Google:ClientSecret",
    "GOOGLE_CLIENT_SECRET",
    "GOOGLE_OAUTH_CLIENT_SECRET"
);
var googleAuthConfigured = !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret);

var authentication = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = googleAuthConfigured
            ? GoogleDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-PawPrints";
        options.Cookie.SameSite = SameSiteMode.None;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/api/auth/login";
        options.LogoutPath = "/api/auth/logout";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

if (googleAuthConfigured)
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = googleClientId!;
        options.ClientSecret = googleClientSecret!;
        options.CallbackPath = "/api/auth/google-callback";
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "AuthenticatedPawPrintsUser",
        policy => policy
            .RequireAuthenticatedUser()
    );
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/auth/login", (string? returnUrl) =>
{
    if (!googleAuthConfigured)
    {
        return Results.Problem(
            title: "Google sign-in is not configured.",
            detail: "Set Authentication__Google__ClientId and Authentication__Google__ClientSecret on the API App Service.",
            statusCode: StatusCodes.Status503ServiceUnavailable
        );
    }

    var properties = new AuthenticationProperties
    {
        RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl,
    };

    return Results.Challenge(properties, [GoogleDefaults.AuthenticationScheme]);
});

app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

app.MapGet("/api/auth/me", [Authorize(Policy = "AuthenticatedPawPrintsUser")] (ClaimsPrincipal user) =>
{
    return Results.Ok(new { email = user.FindFirstValue(ClaimTypes.Email) });
});

app.MapGet(
    "/api/sync",
    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    async (
        CurrentUser currentUser,
        SnapshotSyncService syncService,
        CancellationToken cancellationToken
    ) =>
    {
        var snapshot = await syncService.GetSnapshotAsync(currentUser.Email, cancellationToken);
        return snapshot is null ? Results.NoContent() : Results.Ok(snapshot);
    }
);

app.MapPut(
    "/api/sync",
    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    async (
        SyncSnapshotRequest snapshot,
        CurrentUser currentUser,
        SnapshotSyncService syncService,
        CancellationToken cancellationToken
    ) =>
    {
        await syncService.SyncAsync(currentUser.Email, currentUser.Subject, snapshot, cancellationToken);
        return Results.NoContent();
    }
);

app.Run();

public partial class Program;

static partial class ProgramConfiguration
{
    public static string? GetFirstConfiguredValue(IConfiguration configuration, params string[] keys)
    {
        return keys
            .Select(key => configuration[key])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
