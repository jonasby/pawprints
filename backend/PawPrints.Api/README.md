# PawPrints API

ASP.NET Core API that stores the current frontend snapshot in Azure SQL.

## Authentication

Google authentication is enabled when these app settings are present:

- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`

Only `jon.asby@gmail.com` is authorized by the API policy.

## Database

The production App Service uses `ConnectionStrings__PawPrintsDb` with Azure SQL managed identity.
Local development falls back to `pawprints-local.db` when no connection string is configured.

## CORS

Set `PawPrints__AllowedOrigins` to a comma-separated list of frontend origins, for example the GitHub Pages origin.
