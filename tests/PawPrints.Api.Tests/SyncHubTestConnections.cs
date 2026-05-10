using Microsoft.AspNetCore.SignalR.Client;
using PawPrints.Api.Contracts;

namespace PawPrints.Api.Tests;

internal static class SyncHubTestConnections
{
    public static HubConnection Create(PawPrintsApiApplication application, string? email)
    {
        var hubUri = new Uri(application.Server.BaseAddress!, "hubs/sync");
        return new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.HttpMessageHandlerFactory = _ => application.Server.CreateHandler();
                if (!string.IsNullOrEmpty(email))
                {
                    options.Headers.Add(TestAuthHandler.EmailHeader, email);
                }
            })
            .Build();
    }

    public static async Task PushSnapshotAsync(
        PawPrintsApiApplication application,
        string email,
        SyncSnapshotRequest snapshot
    )
    {
        var hub = Create(application, email);
        await hub.StartAsync();
        try
        {
            await hub.InvokeAsync("PushSnapshot", snapshot);
        }
        finally
        {
            await hub.StopAsync();
        }
    }
}
