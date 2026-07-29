FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FileExplorer.Api.csproj .
RUN dotnet restore FileExplorer.Api.csproj

COPY . .
RUN dotnet publish FileExplorer.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runs as root (the default in this image) - required so the API can browse, chmod and
# chown anywhere under the bind-mounted host filesystem. See ../../README.md.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "FileExplorer.Api.dll"]
