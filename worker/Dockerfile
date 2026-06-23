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

# quick-260417-2lb: runtime needs PowerShell + ExchangeOnlineManagement for DDGResolver.
# That heavy layer now lives in the shared afh-runtime-base image (see base/Dockerfile),
# built once on the box so normal code deploys skip it. Bump the tag here + in
# api/Dockerfile + deploy.sh's BASE_IMAGE when you rebuild the base.
FROM afh-runtime-base:1 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8081
ENTRYPOINT ["dotnet", "AFHSync.Worker.dll"]
