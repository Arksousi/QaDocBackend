# QaDoc API image (ASP.NET Core 9)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY QaDocBackend.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish QaDocBackend.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
USER $APP_UID
# Listen on $PORT when the host provides one (Railway), otherwise 8080.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_HTTP_PORTS=${PORT:-8080} exec dotnet QaDocBackend.dll"]
