# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["src/SteamViewer.Server/SteamViewer.Server.csproj", "src/SteamViewer.Server/"]
COPY ["src/SteamViewer.Common/SteamViewer.Common.csproj", "src/SteamViewer.Common/"]

# Restore dependencies
RUN dotnet restore "src/SteamViewer.Server/SteamViewer.Server.csproj"

# Copy source code
COPY src/SteamViewer.Server/ src/SteamViewer.Server/
COPY src/SteamViewer.Common/ src/SteamViewer.Common/

# Build
WORKDIR /src/src/SteamViewer.Server
RUN dotnet build -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .

# Expose port (Render provides PORT env var)
EXPOSE 8080

ENTRYPOINT ["dotnet", "SteamViewer.Server.dll"]
