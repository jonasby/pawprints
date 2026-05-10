using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PawPrints.Api.Contracts;
using PawPrints.Api.Data;

namespace PawPrints.Api.Tests;

public sealed class SyncApiTests
{
    [Fact]
    public async Task GivenNoLogin_WhenSyncingSnapshot_ThenItIsRejected()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateClient();

        var response = await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GivenAnyGoogleAccount_WhenSyncingSnapshot_ThenUserProfileAndEventsAreStored()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("someone.else@gmail.com");

        var response = await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        response.EnsureSuccessStatusCode();
        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var user = await db.Users.Include(storedUser => storedUser.Events).SingleAsync();
        Assert.Equal("someone.else@gmail.com", user.Email);
        Assert.Equal(3, user.Events.Count);
    }

    [Fact]
    public async Task GivenAllowedGoogleAccount_WhenSyncingSnapshot_ThenUserProfileAndEventsAreStored()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");

        var response = await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        response.EnsureSuccessStatusCode();
        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var user = await db.Users.Include(storedUser => storedUser.Events).SingleAsync();
        Assert.Equal("jon.asby@gmail.com", user.Email);
        Assert.Equal(new DateOnly(2026, 4, 19), user.ArrivalDate);
        Assert.Equal(new DateOnly(2026, 2, 22), user.BirthDate);
        Assert.Equal(
            ["sleep"],
            user.Events
                .Where(storedEvent => storedEvent.DateKey == new DateOnly(2026, 4, 25))
                .Select(storedEvent => storedEvent.Type)
                .ToArray()
        );
        Assert.Equal(
            ["pee", "wake"],
            user.Events
                .Where(storedEvent => storedEvent.DateKey == new DateOnly(2026, 4, 26))
                .Select(storedEvent => storedEvent.Type)
                .Order()
                .ToArray()
        );
    }

    [Fact]
    public async Task GivenExistingEvents_WhenSyncingAChangedSnapshot_ThenTheStoredSnapshotIsReplaced()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");
        await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        var changedSnapshot = CreateSnapshot(
            new SyncEventRequest("evt-food", "eat", DateTimeOffset.Parse("2026-04-26T12:00:00Z"), "2026-04-26")
        );
        var response = await client.PutAsJsonAsync("/api/sync", changedSnapshot);

        response.EnsureSuccessStatusCode();
        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var user = await db.Users.Include(storedUser => storedUser.Events).SingleAsync();
        var storedEvent = Assert.Single(user.Events);
        Assert.Equal("evt-food", storedEvent.ClientEventId);
        Assert.Equal("eat", storedEvent.Type);
    }

    [Fact]
    public async Task GivenStoredSnapshot_WhenFetchingSnapshot_ThenItReturnsSettingsAndEvents()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");
        await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        var snapshot = await client.GetFromJsonAsync<SyncSnapshotRequest>("/api/sync");

        Assert.NotNull(snapshot);
        Assert.Equal("2026-04-19", snapshot.Settings.ArrivalDate);
        Assert.Equal("2026-02-22", snapshot.Settings.BirthDate);
        Assert.Equal(
            ["evt-sleep"],
            snapshot.Events
                .Where(storedEvent => storedEvent.DateKey == "2026-04-25")
                .Select(storedEvent => storedEvent.Id)
                .ToArray()
        );
        Assert.Equal(
            ["evt-pee", "evt-wake"],
            snapshot.Events
                .Where(storedEvent => storedEvent.DateKey == "2026-04-26")
                .Select(storedEvent => storedEvent.Id)
                .Order()
                .ToArray()
        );
    }

    [Fact]
    public async Task GivenStoredEvents_WhenSyncingDeltaUpserts_ThenOnlyTargetedEventsAreUpdated()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");
        await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        var delta = new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            Upserts:
            [
                new SyncEventRequest("evt-wake", "wake", DateTimeOffset.Parse("2026-04-26T07:20:00Z"), "2026-04-26"),
                new SyncEventRequest("evt-food", "eat", DateTimeOffset.Parse("2026-04-26T08:00:00Z"), "2026-04-26"),
            ]
        );

        var response = await client.PutAsJsonAsync("/api/sync", delta);
        response.EnsureSuccessStatusCode();

        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var user = await db.Users.Include(storedUser => storedUser.Events).SingleAsync();

        Assert.Contains(user.Events, stored => stored.ClientEventId == "evt-sleep");
        Assert.Contains(
            user.Events,
            stored => stored.ClientEventId == "evt-wake"
                      && stored.OccurredAt == DateTimeOffset.Parse("2026-04-26T07:20:00Z")
        );
        Assert.Contains(user.Events, stored => stored.ClientEventId == "evt-food" && stored.Type == "eat");
    }

    [Fact]
    public async Task GivenStoredEvents_WhenSyncingDeltaDeletes_ThenOnlyTargetedEventsAreRemoved()
    {
        await using var application = new PawPrintsApiApplication();
        using var client = application.CreateAuthenticatedClient("jon.asby@gmail.com");
        await client.PutAsJsonAsync("/api/sync", CreateSnapshot());

        var delta = new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            DeletedEventIds: ["evt-sleep"]
        );

        var response = await client.PutAsJsonAsync("/api/sync", delta);
        response.EnsureSuccessStatusCode();

        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        var user = await db.Users.Include(storedUser => storedUser.Events).SingleAsync();

        Assert.DoesNotContain(user.Events, stored => stored.ClientEventId == "evt-sleep");
        Assert.Contains(user.Events, stored => stored.ClientEventId == "evt-wake");
        Assert.Contains(user.Events, stored => stored.ClientEventId == "evt-pee");
    }

    private static SyncSnapshotRequest CreateSnapshot(params SyncEventRequest[] events)
    {
        var snapshotEvents = events.Length > 0
            ? events
            : [
                new SyncEventRequest("evt-sleep", "sleep", DateTimeOffset.Parse("2026-04-25T22:00:00Z"), "2026-04-25"),
                new SyncEventRequest("evt-wake", "wake", DateTimeOffset.Parse("2026-04-26T07:00:00Z"), "2026-04-26"),
                new SyncEventRequest("evt-pee", "pee", DateTimeOffset.Parse("2026-04-26T07:00:00Z"), "2026-04-26"),
            ];

        return new SyncSnapshotRequest(
            new SyncSettingsRequest("2026-04-19", "2026-02-22"),
            snapshotEvents
        );
    }
}

public sealed class PawPrintsApiApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TimeProvider? _timeProvider;

    public PawPrintsApiApplication(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
        _connection.Open();
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    public HttpClient CreateAuthenticatedClient(string email)
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        }).WithEmail(email);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var dbContextDescriptor = services.SingleOrDefault(service =>
                service.ServiceType == typeof(DbContextOptions<PawPrintsDbContext>));
            if (dbContextDescriptor is not null)
            {
                services.Remove(dbContextDescriptor);
            }

            services.AddDbContext<PawPrintsDbContext>(options => options.UseSqlite(_connection));
            if (_timeProvider is not null)
            {
                var timeProviderDescriptor = services.SingleOrDefault(service =>
                    service.ServiceType == typeof(TimeProvider));
                if (timeProviderDescriptor is not null)
                {
                    services.Remove(timeProviderDescriptor);
                }

                services.AddSingleton(_timeProvider);
            }

            var hostedServiceDescriptors = services
                .Where(service => service.ServiceType == typeof(IHostedService))
                .ToArray();
            foreach (var hostedServiceDescriptor in hostedServiceDescriptors)
            {
                services.Remove(hostedServiceDescriptor);
            }

            services.AddHostedService<SqliteSchemaInitializer>();
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddPolicyScheme("DefaultTestScheme", null, options =>
                {
                    options.ForwardDefault = TestAuthHandler.SchemeName;
                });
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { }
                );
        });
    }
}

public sealed class SqliteSchemaInitializer(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PawPrintsDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public static class HttpClientAuthExtensions
{
    public static HttpClient WithEmail(this HttpClient client, string email)
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        return client;
    }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string EmailHeader = "X-Test-Email";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(EmailHeader, out var emailValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var email = emailValues.Single()!;
        var claims = new[]
        {
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.NameIdentifier, $"google-{email}"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
