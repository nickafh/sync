FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["worker/AFHSync.Worker.csproj", "worker/"]
COPY ["shared/AFHSync.Shared.csproj", "shared/"]
RUN dotnet restore "worker/AFHSync.Worker.csproj"
COPY worker/ worker/
COPY shared/ shared/
RUN dotnet publish "worker/AFHSync.Worker.csproj" -c Release -o /app/publish

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
