FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props .
COPY src/SmartPalm.ApiGateway/SmartPalm.ApiGateway.csproj src/SmartPalm.ApiGateway/
RUN dotnet restore src/SmartPalm.ApiGateway/SmartPalm.ApiGateway.csproj
COPY . .
RUN dotnet publish src/SmartPalm.ApiGateway/SmartPalm.ApiGateway.csproj -c Release -o /app/publish --no-restore

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "SmartPalm.ApiGateway.dll"]
