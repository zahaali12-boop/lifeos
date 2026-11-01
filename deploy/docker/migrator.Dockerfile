FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Quicker.sln ./
COPY src ./src
COPY db ./db
RUN dotnet publish src/Host/Quicker.Migrator/Quicker.Migrator.csproj -c Release -o /out

# The demo seeder composes the modules (ASP.NET Core services), so the runtime image is the aspnet one.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# The command is the migrator's verb: `all` by default (migrate, seed, create the demo tenant when missing);
# `docker compose run --rm migrator demo` rebuilds the demo tenant, `... migrator verify` runs the invariant harness.
ENTRYPOINT ["dotnet", "Quicker.Migrator.dll"]
CMD ["all"]
