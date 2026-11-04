# The program's three .NET services (the API, the worker, the migrator) from ONE compile: the three hosts share every
# module, so publishing them one after another in the same stage compiles the modules once instead of three times, and
# Compose builds the stage once for all three targets (api, worker, migrator).
#
# What CI already checks (the analyzers, the style rules, warnings as errors, the OpenAPI document written at build) is
# switched off here: it roughly halves the build and changes nothing in the program that runs. The NuGet packages are
# kept between builds, so an update downloads nothing it already has.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Quicker.sln ./
COPY src ./src
COPY db ./db
RUN --mount=type=cache,target=/root/.nuget/packages \
    set -e; \
    for host in Api:api Worker:worker Migrator:migrator; do \
      project="${host%%:*}"; out="${host##*:}"; \
      dotnet publish "src/Host/Quicker.${project}/Quicker.${project}.csproj" -c Release -o "/out/${out}" \
        -p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false -p:TreatWarningsAsErrors=false \
        -p:OpenApiGenerateDocumentsOnBuild=false; \
    done

# The demo seeder composes the modules (ASP.NET Core services), so every runtime image is the aspnet one.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

FROM runtime AS api
COPY --from=build /out/api .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "Quicker.Api.dll"]

FROM runtime AS worker
COPY --from=build /out/worker .
EXPOSE 8081
ENV ASPNETCORE_URLS=http://0.0.0.0:8081
ENTRYPOINT ["dotnet", "Quicker.Worker.dll"]

FROM runtime AS migrator
COPY --from=build /out/migrator .
# The command is the migrator's verb: `all` by default (migrate, seed, create the demo tenant when missing);
# `docker compose run --rm migrator demo` rebuilds the demo tenant, `... migrator verify` runs the invariant harness.
ENTRYPOINT ["dotnet", "Quicker.Migrator.dll"]
CMD ["all"]
