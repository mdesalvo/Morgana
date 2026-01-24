# ==============================================================================
# MORGANA - SIGNALR BACKEND DOCKERFILE
# ==============================================================================
# Multi-stage build for optimized image with all required projects:
# - Morgana.SignalR (API + SignalR Hub)
# - Morgana.Framework (core framework)
# - Morgana.Example (example plugin with agents)

# ==============================================================================
# STAGE 1: BUILD
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for all required projects (layer caching optimization)
COPY ["Morgana/Morgana.SignalR/Morgana.SignalR.csproj", "Morgana.SignalR/"]
COPY ["Morgana/Morgana.Framework/Morgana.Framework.csproj", "Morgana.Framework/"]
COPY ["Morgana/Morgana.Example/Morgana.Example.csproj", "Morgana.Example/"]
COPY ["Morgana/Morgana.Framework/Directory.Build.props", "Morgana.Framework/"]

# Restore NuGet dependencies
RUN dotnet restore "Morgana.SignalR/Morgana.SignalR.csproj"

# Copy all source code from all projects
COPY Morgana/Morgana.SignalR/ Morgana.SignalR/
COPY Morgana/Morgana.Framework/ Morgana.Framework/
COPY Morgana/Morgana.Example/ Morgana.Example/

# Build main project (automatically includes dependencies)
WORKDIR "/src/Morgana.SignalR"
RUN dotnet build "Morgana.SignalR.csproj" -c Release -o /app/build

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish
RUN dotnet publish "Morgana.SignalR.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Expose port 5001 for HTTP
EXPOSE 5001

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Create directory for SQLite databases (conversation persistence)
RUN mkdir -p /app/data

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5001
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Morgana.SignalR.dll"]
