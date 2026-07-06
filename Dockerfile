# EVE Together server — distributed as a Docker image.
# Multi-stage build: SDK image compiles and publishes, the smaller ASP.NET runtime image runs it.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore EveUtils.Server/EveUtils.Server.csproj
RUN dotnet publish EveUtils.Server/EveUtils.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Data directory: SQLite database, self-signed TLS cert, app log, ESI cache and auth store all live here.
# Mount a volume at /data to persist them across container recreation.
ENV EVEUTILS_SERVER_DATA_DIR=/data \
    ASPNETCORE_ENVIRONMENT=Production

# Run as the image's non-root user; give it ownership of the data directory.
RUN mkdir -p /data && chown app:app /data
USER app

VOLUME /data
EXPOSE 7443
ENTRYPOINT ["dotnet", "EveUtils.Server.dll"]
