FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .
COPY runtime/ runtime/
RUN dotnet restore runtime/Tpcli.Watchdog/Tpcli.Watchdog.csproj --locked-mode
RUN dotnet publish runtime/Tpcli.Watchdog/Tpcli.Watchdog.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Tpcli.Watchdog.dll"]
