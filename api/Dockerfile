FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["api/AFHSync.Api.csproj", "api/"]
COPY ["shared/AFHSync.Shared.csproj", "shared/"]
RUN dotnet restore "api/AFHSync.Api.csproj"
COPY api/ api/
COPY shared/ shared/
RUN dotnet publish "api/AFHSync.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AFHSync.Api.dll"]
