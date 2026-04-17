FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# quick-260417-2lb: worker now references api (for IDDGResolver) — copy api csproj + sources too.
COPY ["worker/AFHSync.Worker.csproj", "worker/"]
COPY ["api/AFHSync.Api.csproj", "api/"]
COPY ["shared/AFHSync.Shared.csproj", "shared/"]
RUN dotnet restore "worker/AFHSync.Worker.csproj"
COPY worker/ worker/
COPY api/ api/
COPY shared/ shared/
RUN dotnet publish "worker/AFHSync.Worker.csproj" -c Release -o /app/publish

# quick-260417-2lb: switched runtime base from aspnet:10.0-alpine to aspnet:10.0-noble
# because DDGResolver (now linked in via api ProjectReference) requires PowerShell +
# the ExchangeOnlineManagement module to resolve DDG membership live during sync.
# Mirrors api/Dockerfile's runtime layer.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    libkrb5-3 curl wget apt-transport-https && \
    wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && rm /tmp/packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y --no-install-recommends powershell && \
    pwsh -c "Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module ExchangeOnlineManagement -Force -AcceptLicense -Scope AllUsers" && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8081
ENTRYPOINT ["dotnet", "AFHSync.Worker.dll"]
