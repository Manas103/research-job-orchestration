FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore ResearchOrchestration.sln
RUN dotnet publish src/Orchestrator.Api/Orchestrator.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Without this, ASP.NET Core's config file watcher (FileSystemWatcher over appsettings.json)
# can exhaust a container's inotify instance budget and crash at startup; see README Findings.
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
ENTRYPOINT ["dotnet", "Orchestrator.Api.dll"]
