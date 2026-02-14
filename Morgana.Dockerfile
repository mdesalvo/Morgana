# ==============================================================================
# MORGANA - SIGNALR BACKEND DOCKERFILE
# ==============================================================================
# Multi-stage build for optimized image with all required projects:
# - Morgana.SignalR (API + SignalR Hub)
# - Morgana.Framework (core framework)
# - Morgana.Examples (example plugins with 3 agents)
# ==============================================================================

# ==============================================================================
# BUILD ARGUMENTS
# ==============================================================================
ARG VERSION=latest

# ==============================================================================
# STAGE 1: BUILD
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for all required projects (layer caching optimization)
COPY ["Morgana/Morgana.SignalR/Morgana.SignalR.csproj", "Morgana.SignalR/"]
COPY ["Morgana/Morgana.Framework/Morgana.Framework.csproj", "Morgana.Framework/"]
COPY ["Morgana/Morgana.Examples/Morgana.Examples.csproj", "Morgana.Examples/"]
COPY ["Morgana/Directory.Build.props", "Directory.Build.props"]

# Restore NuGet dependencies
RUN dotnet restore "Morgana.SignalR/Morgana.SignalR.csproj"

# Copy all source code from all projects
COPY Morgana/Morgana.SignalR/ Morgana.SignalR/
COPY Morgana/Morgana.Framework/ Morgana.Framework/
COPY Morgana/Morgana.Examples/ Morgana.Examples/

# Build main project
WORKDIR "/src/Morgana.SignalR"
RUN dotnet build "Morgana.SignalR.csproj" -c Release -o /app/build

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish

# Publish Morgana.SignalR (main application)
WORKDIR "/src/Morgana.SignalR"
RUN dotnet publish "Morgana.SignalR.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Publish Morgana.Examples to plugins/ directory
WORKDIR "/src/Morgana.Examples"
RUN dotnet publish "Morgana.Examples.csproj" \
    -c Release \
    -o /app/publish/plugins \
    /p:UseAppHost=false

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Morgana"
LABEL org.opencontainers.image.description="A magical witch assistant equipped with an enchanted AI-driven grimoire (BackEnd)"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.authors="Marco De Salvo"
LABEL org.opencontainers.image.url="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.source="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.licenses="Apache-2.0"

# Expose port 5001 for HTTP
EXPOSE 5001

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Create directory for SQLite databases (conversation persistence)
RUN mkdir -p /app/data

# Verify plugins directory exists and contains Morgana.Examples.dll
RUN ls -la /app/plugins/ && \
    test -f /app/plugins/Morgana.Examples.dll || \
    (echo "ERROR: Morgana.Examples.dll not found in plugins/" && exit 1)

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5001
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Morgana.SignalR.dll"]
