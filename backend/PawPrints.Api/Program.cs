using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PawPrints.Api;
using PawPrints.Api.Data;
using PawPrints.Api.Import;
using PawPrints.Api.Invites;
using PawPrints.Api.Middleware;
using Serilog;
using Serilog.Formatting.Compact;

const string AuthenticatedPawPrintsUserPolicy = "AuthenticatedPawPrintsUser";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

try
{
    Log.Information("Starting PawPrints API");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog(
        (context, services, configuration) =>
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .WriteTo.Console(new RenderedCompactJsonFormatter()),
        preserveStaticLogger: true);

    var isDevelopment = builder.Environment.IsDevelopment();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<CurrentUser>();
    builder.Services.AddScoped<SnapshotSyncService>();
    builder.Services.AddScoped<InviteService>();
    builder.Services.AddHttpClient<ImportTokenResolveService>();
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
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PawPrints.Auth.Cookie");
                logger.LogWarning(
                    "Cookie auth challenge for {Path}. IsAuthenticated={IsAuthenticated}. HasCookieHeader={HasCookieHeader}. Origin={Origin}. Referer={Referer}. UserAgent={UserAgent}.",
                    context.Request.Path,
                    context.HttpContext.User.Identity?.IsAuthenticated ?? false,
                    context.Request.Headers.ContainsKey("Cookie"),
                    context.Request.Headers.Origin.ToString(),
                    context.Request.Headers.Referer.ToString(),
                    context.Request.Headers.UserAgent.ToString()
                );

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
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PawPrints.Auth.Cookie");
                logger.LogWarning(
                    "Cookie auth access denied for {Path}. IsAuthenticated={IsAuthenticated}. HasCookieHeader={HasCookieHeader}.",
                    context.Request.Path,
                    context.HttpContext.User.Identity?.IsAuthenticated ?? false,
                    context.Request.Headers.ContainsKey("Cookie")
                );

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
            options.Events.OnTicketReceived = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PawPrints.Auth.Google");
                logger.LogInformation(
                    "Google ticket received. RedirectUri={RedirectUri}. HasCookieHeader={HasCookieHeader}. Email={Email}.",
                    context.Properties?.RedirectUri,
                    context.Request.Headers.ContainsKey("Cookie"),
                    context.Principal?.FindFirstValue(ClaimTypes.Email) ?? "(missing)"
                );
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("PawPrints.Auth.Google");
                logger.LogWarning(
                    context.Failure,
                    "Google remote failure. FailureMessage={FailureMessage}. Path={Path}. Query={Query}.",
                    context.Failure?.Message,
                    context.Request.Path,
                    context.Request.QueryString.ToString()
                );
                return Task.CompletedTask;
            };
        });
    }

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(
            AuthenticatedPawPrintsUserPolicy,
            policy => policy
                .RequireAuthenticatedUser()
        );
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var useEnsureCreated = app.Environment.IsEnvironment("Testing")
            || ProgramConfiguration.ShouldUseEnsureCreated(db.Database.ProviderName);
        if (useEnsureCreated)
        {
            startupLogger.LogInformation(
                "Initializing database with EnsureCreated for provider {DatabaseProvider}",
                db.Database.ProviderName ?? "unknown"
            );
            if (ProgramConfiguration.ShouldUseEnsureCreated(db.Database.ProviderName))
            {
                var existingTables = await ProgramConfiguration.GetSqliteTableNamesAsync(
                    db.Database.GetDbConnection(),
                    CancellationToken.None
                );
                if (ProgramConfiguration.ShouldRecreateSqliteSchema(existingTables))
                {
                    startupLogger.LogInformation("Detected incomplete SQLite schema; recreating local database");
                    await db.Database.EnsureDeletedAsync();
                }
            }
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            startupLogger.LogInformation(
                "Applying migrations for provider {DatabaseProvider}",
                db.Database.ProviderName ?? "unknown"
            );
            try
            {
                await db.Database.MigrateAsync();
            }
            catch (Exception ex)
            {
                Program.LogDatabaseMigrationFailure(startupLogger, ex);
                throw;
            }
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseMiddleware<UnhandledExceptionLoggingMiddleware>();

    app.UseHttpsRedirection();
    app.UseCors("Frontend");
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/api/auth/login", (string? returnUrl) =>
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PawPrints.Auth.Login");
        logger.LogInformation("Auth login requested. ReturnUrl={ReturnUrl}.", returnUrl);

        if (!googleAuthConfigured)
        {
            logger.LogWarning("Auth login rejected because Google sign-in is not configured.");
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
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PawPrints.Auth.Logout");
        logger.LogInformation(
            "Auth logout requested. IsAuthenticatedBeforeLogout={IsAuthenticated}. HasCookieHeader={HasCookieHeader}.",
            httpContext.User.Identity?.IsAuthenticated ?? false,
            httpContext.Request.Headers.ContainsKey("Cookie")
        );
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    });

    app.MapGet(
        "/api/auth/me",
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
        async (
            ClaimsPrincipal user,
            HttpContext httpContext,
            PawPrintsDbContext dbContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) =>
        {
            var meLogger = loggerFactory.CreateLogger("PawPrints.Auth.Me");
            var email = user.FindFirstValue(ClaimTypes.Email)!;
            var storedUser = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Email == email,
                cancellationToken
            );

            if (storedUser is null)
            {
                meLogger.LogInformation(
                    "Current user profile load completed with outcome {Outcome} email {Email} collaboration role {CollaborationRole} owner profile email {OwnerProfileEmail} has cookie header {HasCookieHeader} origin {Origin} referer {Referer}",
                    "NotPersistedYet",
                    email,
                    "owner",
                    null,
                    httpContext.Request.Headers.ContainsKey("Cookie"),
                    httpContext.Request.Headers.Origin.ToString(),
                    httpContext.Request.Headers.Referer.ToString()
                );
                return Results.Ok(new MeResponse(email, new CollaborationInfo("owner", null)));
            }

            if (storedUser.CollaboratesWithUserId is null)
            {
                meLogger.LogInformation(
                    "Current user profile load completed with outcome {Outcome} email {Email} collaboration role {CollaborationRole} owner profile email {OwnerProfileEmail} has cookie header {HasCookieHeader} origin {Origin} referer {Referer}",
                    "Owner",
                    email,
                    "owner",
                    null,
                    httpContext.Request.Headers.ContainsKey("Cookie"),
                    httpContext.Request.Headers.Origin.ToString(),
                    httpContext.Request.Headers.Referer.ToString()
                );
                return Results.Ok(new MeResponse(email, new CollaborationInfo("owner", null)));
            }

            var owner = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == storedUser.CollaboratesWithUserId,
                cancellationToken
            );

            meLogger.LogInformation(
                "Current user profile load completed with outcome {Outcome} email {Email} collaboration role {CollaborationRole} owner profile email {OwnerProfileEmail} has cookie header {HasCookieHeader} origin {Origin} referer {Referer}",
                "Collaborator",
                email,
                "collaborator",
                owner?.Email,
                httpContext.Request.Headers.ContainsKey("Cookie"),
                httpContext.Request.Headers.Origin.ToString(),
                httpContext.Request.Headers.Referer.ToString()
            );

            return Results.Ok(new MeResponse(email, new CollaborationInfo("collaborator", owner?.Email)));
        }
    );

    app.MapPost(
        "/api/invites",
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
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
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
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
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
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
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
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

    app.MapPost(
        "/api/import/resolve-tokens",
        [Authorize(Policy = AuthenticatedPawPrintsUserPolicy)]
        async (
            ImportResolveTokensRequest request,
            ImportTokenResolveService resolver,
            CancellationToken cancellationToken
        ) =>
        {
            var response = await resolver.ResolveAsync(request, cancellationToken);
            return Results.Ok(response);
        }
    );

    // Serve SPA routes from the same host/App Service while keeping /api endpoints handled above.
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception fatal)
{
    Log.Fatal(fatal, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
    /// <summary>
    /// Logs EF migration failures with inner exceptions and, when present, SQL Server error numbers and batch errors
    /// (avoids opaque “connection reset” style failures with no root cause in logs).
    /// </summary>
    public static void LogDatabaseMigrationFailure(Microsoft.Extensions.Logging.ILogger logger, Exception exception)
    {
        logger.LogError(
            exception,
            "Database migration failed with {ExceptionType}: {Message}",
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message
        );

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                logger.LogError(
                    inner,
                    "Migration aggregate inner: {ExceptionType}: {Message}",
                    inner.GetType().FullName ?? inner.GetType().Name,
                    inner.Message
                );
                LogSqlClientErrorsIfPresent(logger, inner);
            }

            return;
        }

        for (var walk = exception.InnerException; walk != null; walk = walk.InnerException)
        {
            logger.LogError(
                "Migration inner exception: {ExceptionType}: {Message}",
                walk.GetType().FullName ?? walk.GetType().Name,
                walk.Message
            );
        }

        LogSqlClientErrorsIfPresent(logger, exception);
    }

    static void LogSqlClientErrorsIfPresent(Microsoft.Extensions.Logging.ILogger logger, Exception exception)
    {
        var sql = FindSqlException(exception);
        if (sql is null)
        {
            return;
        }

        logger.LogError(
            "SqlException: Number={SqlNumber}, State={SqlState}, Class={SqlClass}, LineNumber={SqlLineNumber}, Source={SqlSource}, Server={SqlServer}, Procedure={SqlProcedure}",
            sql.Number,
            sql.State,
            sql.Class,
            sql.LineNumber,
            sql.Source,
            sql.Server,
            sql.Procedure ?? "(none)"
        );

        for (var i = 0; i < sql.Errors.Count; i++)
        {
            var err = sql.Errors[i];
            logger.LogError(
                "SqlError[{ErrorIndex}]: Number={Number}, State={State}, Class={Class}, Message={SqlMessage}",
                i,
                err.Number,
                err.State,
                err.Class,
                err.Message
            );
        }
    }

    static SqlException? FindSqlException(Exception exception)
    {
        if (exception is SqlException sql)
        {
            return sql;
        }

        for (var walk = exception.InnerException; walk != null; walk = walk.InnerException)
        {
            if (walk is SqlException innerSql)
            {
                return innerSql;
            }
        }

        return null;
    }
}

public static partial class ProgramConfiguration
{
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
