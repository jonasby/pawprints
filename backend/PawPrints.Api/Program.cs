using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;
using PawPrints.Api.Invites;
using PawPrints.Api.Sync;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
var dataProtectionKeysPath = ProgramConfiguration.GetDataProtectionKeysPath();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services
        .AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
        .SetApplicationName("PawPrints");
    Console.WriteLine($"Persisting ASP.NET Data Protection keys at '{dataProtectionKeysPath}'.");
}
else
{
    Console.WriteLine("Using default ASP.NET Data Protection key storage.");
}
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<SnapshotSyncService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173", "https://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddDbContext<PawPrintsDbContext>(options =>
{
    var configuredConnectionString = builder.Configuration.GetConnectionString("PawPrintsDb");
    var useSqliteFallback = ProgramConfiguration.ShouldUseSqliteForDevelopment(
        configuredConnectionString,
        isDevelopment
    );
    var connectionString = useSqliteFallback ? null : configuredConnectionString;

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Using local SQLite database for PawPrints API.");
        options.UseSqlite("Data Source=pawprints-local.db");
        return;
    }

    Console.WriteLine("Using configured SQL Server database for PawPrints API.");
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
        // Use Cookie for challenges so /api/* gets 401 (see cookie OnRedirectToLogin), not a 302 to
        // Google OAuth. SPA fetch cannot follow Google's authorization URL (CORS). Explicit login
        // uses Results.Challenge(..., GoogleDefaults.AuthenticationScheme) on /api/auth/login.
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        // Local HTTP development cannot use __Host- prefixed secure-only cookies.
        // Production keeps stricter cookie settings.
        options.Cookie.Name = isDevelopment ? "PawPrints.Dev" : "__Host-PawPrints";
        options.Cookie.SameSite = isDevelopment ? SameSiteMode.Lax : SameSiteMode.None;
        options.Cookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
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
    var useEnsureCreated = app.Environment.IsEnvironment("Testing")
        || ProgramConfiguration.ShouldUseEnsureCreated(db.Database.ProviderName);
    if (useEnsureCreated)
    {
        Console.WriteLine($"Initializing database with EnsureCreated for provider '{db.Database.ProviderName}'.");
        if (ProgramConfiguration.ShouldUseEnsureCreated(db.Database.ProviderName))
        {
            var existingTables = await ProgramConfiguration.GetSqliteTableNamesAsync(
                db.Database.GetDbConnection(),
                CancellationToken.None
            );
            if (ProgramConfiguration.ShouldRecreateSqliteSchema(existingTables))
            {
                Console.WriteLine("Detected incomplete SQLite schema. Recreating local database.");
                await db.Database.EnsureDeletedAsync();
            }
        }
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        Console.WriteLine($"Applying migrations for provider '{db.Database.ProviderName}'.");
        await db.Database.MigrateAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
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

app.MapGet(
    "/api/auth/me",
    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    async (
        ClaimsPrincipal user,
        PawPrintsDbContext dbContext,
        CancellationToken cancellationToken
    ) =>
    {
        var email = user.FindFirstValue(ClaimTypes.Email)!;
        var storedUser = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Email == email,
            cancellationToken
        );

        if (storedUser is null || storedUser.CollaboratesWithUserId is null)
        {
            return Results.Ok(new MeResponse(email, new CollaborationInfo("owner", null)));
        }

        var owner = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == storedUser.CollaboratesWithUserId,
            cancellationToken
        );

        return Results.Ok(new MeResponse(email, new CollaborationInfo("collaborator", owner?.Email)));
    }
);

app.MapPost(
    "/api/invites",
    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    async (
        CurrentUser currentUser,
        InviteService inviteService,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            var created = await inviteService.CreateInviteAsync(currentUser.Email, cancellationToken);
            if (created is null)
            {
                return Results.Problem(
                    title: "Profile not found.",
                    detail: "Save your puppy log once before sharing it.",
                    statusCode: StatusCodes.Status404NotFound
                );
            }

            return Results.Created("/api/invites", created);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(
                title: "Cannot create invite.",
                detail: exception.Message,
                statusCode: StatusCodes.Status403Forbidden
            );
        }
    }
);

app.MapPost(
    "/api/invites/{token}/accept",
    [Authorize(Policy = "AuthenticatedPawPrintsUser")]
    async (
        string token,
        CurrentUser currentUser,
        InviteService inviteService,
        CancellationToken cancellationToken
    ) =>
    {
        var outcome = await inviteService.AcceptInviteAsync(
            token,
            currentUser.Email,
            currentUser.Subject,
            cancellationToken
        );

        if (!outcome.Success)
        {
            return Results.Problem(title: "Invite cannot be accepted.", detail: outcome.Error, statusCode: outcome.StatusCode);
        }

        return Results.NoContent();
    }
);

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

public static partial class ProgramConfiguration
{
    public static string? GetDataProtectionKeysPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, "ASP.NET", "DataProtection-Keys");
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "PawPrints", "DataProtection-Keys");
        }

        return null;
    }

    public static string? GetFirstConfiguredValue(IConfiguration configuration, params string[] keys)
    {
        return keys
            .Select(key => configuration[key])
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    public static bool ShouldUseSqliteForDevelopment(string? connectionString, bool isDevelopment)
    {
        return isDevelopment
            && !string.IsNullOrWhiteSpace(connectionString)
            && connectionString.Contains(
                "Authentication=Active Directory Managed Identity",
                StringComparison.OrdinalIgnoreCase
            );
    }

    public static bool ShouldUseEnsureCreated(string? providerName)
    {
        return string.Equals(
            providerName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal
        );
    }

    public static bool ShouldRecreateSqliteSchema(IEnumerable<string> existingTables)
    {
        var tableNames = existingTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !tableNames.Contains("Users")
            || !tableNames.Contains("Events")
            || !tableNames.Contains("Invites");
    }

    public static async Task<IReadOnlyList<string>> GetSqliteTableNamesAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        var shouldCloseConnection = connection.State != System.Data.ConnectionState.Open;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        var tableNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tableNames.Add(reader.GetString(0));
        }

        if (shouldCloseConnection)
        {
            await connection.CloseAsync();
        }

        return tableNames;
    }
}
