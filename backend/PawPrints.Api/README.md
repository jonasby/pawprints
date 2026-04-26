# PawPrints API

ASP.NET Core API that stores the current frontend snapshot in Azure SQL.

## Authentication

Google authentication is enabled when these app settings are present:

- `Authentication__Google__ClientId`
- `Authentication__Google__ClientSecret`

The API also accepts the common alternatives `Google__ClientId` /
`Google__ClientSecret` and `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`.

Google OAuth controls who can sign in. The API requires an authenticated Google
principal and stores a separate snapshot per email address.

## Database

The production App Service uses `ConnectionStrings__PawPrintsDb` with Azure SQL managed identity.
Local development falls back to `pawprints-local.db` when no connection string is configured.

## Hosting

The API serves the built Vite frontend from `wwwroot` in production so the app
and API share the same `azurewebsites.net` origin. That keeps the auth cookie
first-party after Google sign-in, including on mobile browsers.

## CORS

Set `AllowedOrigins__0`, `AllowedOrigins__1`, and so on for any separate frontend origins.
