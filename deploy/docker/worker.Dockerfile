FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Quicker.sln ./
COPY src ./src
COPY db ./db
RUN dotnet publish src/Host/Quicker.Worker/Quicker.Worker.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 8081
ENV ASPNETCORE_URLS=http://0.0.0.0:8081
ENTRYPOINT ["dotnet", "Quicker.Worker.dll"]
