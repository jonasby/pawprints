namespace PawPrints.Api.Tests;

public sealed class ProgramConfigurationTests
{
    [Fact]
    public void GivenDevelopmentAndManagedIdentityConnectionString_WhenChoosingDatabase_ThenUseSqliteFallback()
    {
        var useSqlite = ProgramConfiguration.ShouldUseSqliteForDevelopment(
            "Server=tcp:example.database.windows.net,1433;Authentication=Active Directory Managed Identity;",
            isDevelopment: true
        );

        Assert.True(useSqlite);
    }

    [Fact]
    public void GivenDevelopmentAndNonManagedIdentityConnectionString_WhenChoosingDatabase_ThenKeepConfiguredDatabase()
    {
        var useSqlite = ProgramConfiguration.ShouldUseSqliteForDevelopment(
            "Server=tcp:example.database.windows.net,1433;Authentication=Active Directory Default;",
            isDevelopment: true
        );

        Assert.False(useSqlite);
    }

    [Fact]
    public void GivenProductionAndManagedIdentityConnectionString_WhenChoosingDatabase_ThenKeepConfiguredDatabase()
    {
        var useSqlite = ProgramConfiguration.ShouldUseSqliteForDevelopment(
            "Server=tcp:example.database.windows.net,1433;Authentication=Active Directory Managed Identity;",
            isDevelopment: false
        );

        Assert.False(useSqlite);
    }

    [Fact]
    public void GivenSqliteProvider_WhenChoosingInitializationStrategy_ThenUseEnsureCreated()
    {
        var useEnsureCreated = ProgramConfiguration.ShouldUseEnsureCreated("Microsoft.EntityFrameworkCore.Sqlite");

        Assert.True(useEnsureCreated);
    }

    [Fact]
    public void GivenSqlServerProvider_WhenChoosingInitializationStrategy_ThenUseMigrations()
    {
        var useEnsureCreated = ProgramConfiguration.ShouldUseEnsureCreated("Microsoft.EntityFrameworkCore.SqlServer");

        Assert.False(useEnsureCreated);
    }

    [Fact]
    public void GivenSqliteMissingUsersTable_WhenCheckingSchemaRecovery_ThenRecreateIsRequired()
    {
        var shouldRecreate = ProgramConfiguration.ShouldRecreateSqliteSchema(
            ["__EFMigrationsHistory", "Events"]
        );

        Assert.True(shouldRecreate);
    }

    [Fact]
    public void GivenSqliteRequiredTablesPresent_WhenCheckingSchemaRecovery_ThenRecreateIsNotRequired()
    {
        var shouldRecreate = ProgramConfiguration.ShouldRecreateSqliteSchema(
            ["Users", "Events", "Invites", "Predictions", "NotificationOutbox", "__EFMigrationsHistory"]
        );

        Assert.False(shouldRecreate);
    }
}
