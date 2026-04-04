FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["worker/AFHSync.Worker.csproj", "worker/"]
COPY ["shared/AFHSync.Shared.csproj", "shared/"]
RUN dotnet restore "worker/AFHSync.Worker.csproj"
COPY worker/ worker/
COPY shared/ shared/
RUN dotnet publish "worker/AFHSync.Worker.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AFHSync.Worker.dll"]
