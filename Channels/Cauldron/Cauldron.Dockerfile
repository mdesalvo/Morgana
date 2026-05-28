# ==============================================================================
# CAULDRON - BLAZOR SERVER DOCKERFILE
# ==============================================================================
# Multi-stage build to optimize final image size
# Stage 1: Build with full SDK (~1 GB)
# Stage 2: Runtime with ASP.NET Core Runtime only (~200 MB)

# ==============================================================================
# BUILD ARGUMENTS
# ==============================================================================
# Version is passed from docker-compose or GitHub Actions
ARG VERSION=latest

# ==============================================================================
# STAGE 1: BUILD
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and dependencies (for optimal layer caching). The repo layout is
# mirrored under /src so Cauldron's ProjectReference to ../../Morgana/Morgana.Contracts
# resolves, and each project picks up its own Directory.Build.props (Cauldron's vs the
# Morgana one that the zero-dependency Morgana.Contracts inherits).
COPY ["Channels/Cauldron/Cauldron.csproj", "Channels/Cauldron/"]
COPY ["Channels/Cauldron/Directory.Build.props", "Channels/Cauldron/"]
COPY ["Morgana/Morgana.Contracts/Morgana.Contracts.csproj", "Morgana/Morgana.Contracts/"]
COPY ["Morgana/Directory.Build.props", "Morgana/"]

# Restore NuGet dependencies (cached layer if .csproj files don't change)
RUN dotnet restore "Channels/Cauldron/Cauldron.csproj"

# Copy all source code (channel + the referenced wire-contract project)
COPY Channels/Cauldron/ Channels/Cauldron/
COPY Morgana/Morgana.Contracts/ Morgana/Morgana.Contracts/

# Build application in Release mode — InsideDockerBuild skips
# Directory.Build.targets' host-side .env.versions generation, which can't see
# sibling projects here.
WORKDIR "/src/Channels/Cauldron"
RUN dotnet build "Cauldron.csproj" -c Release -o /app/build /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish
RUN dotnet publish "Cauldron.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Cauldron"
LABEL org.opencontainers.image.description="A magical witch assistant equipped with an enchanted AI-driven grimoire (FrontEnd)"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.authors="Marco De Salvo"
LABEL org.opencontainers.image.url="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.source="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.licenses="Apache-2.0"

# Expose port 5002 for HTTP
EXPOSE 5002

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5002
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Cauldron.dll"]
