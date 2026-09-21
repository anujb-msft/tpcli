FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .
COPY runtime/ runtime/
RUN dotnet restore runtime/Tpcli.Runtime/Tpcli.Runtime.csproj --locked-mode
RUN dotnet publish runtime/Tpcli.Runtime/Tpcli.Runtime.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Tpcli.Runtime.dll"]
