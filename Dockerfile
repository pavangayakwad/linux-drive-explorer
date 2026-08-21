FROM node:24-alpine AS frontend-build
WORKDIR /app
COPY frontend/package*.json ./
RUN npm install
COPY frontend/ .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/FileExplorer.Api/FileExplorer.Api.csproj .
RUN dotnet restore FileExplorer.Api.csproj
COPY backend/FileExplorer.Api/ .
RUN dotnet publish FileExplorer.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runs as root (the default in this image) - required so the API can browse, chmod and
# chown anywhere under the bind-mounted host filesystem. See README.md.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=backend-build /app/publish .
# The Angular build lands in wwwroot, where Kestrel's static-file middleware serves it
# alongside the API/SignalR routes - no separate web server needed.
COPY --from=frontend-build /app/dist/frontend/browser ./wwwroot

ENTRYPOINT ["dotnet", "FileExplorer.Api.dll"]
