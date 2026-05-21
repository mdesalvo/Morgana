# ==============================================================================
# GRIMOIRE - CLI WEBHOOK CHANNEL DOCKERFILE
# ==============================================================================
# Multi-stage build to optimize final image size
# Stage 1: Build with full SDK (~1 GB)
# Stage 2: Runtime with ASP.NET Core Runtime only (~200 MB)
#
# Build context: repository root (same as Morgana.Dockerfile and Cauldron.Dockerfile).
# All internal COPY paths are therefore relative to the repo root.

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

# Copy project file and dependencies (for optimal layer caching)
COPY ["Channels/Grimoire/Grimoire.csproj", "Grimoire/"]
COPY ["Channels/Grimoire/Directory.Build.props", "Directory.Build.props"]

# Restore NuGet dependencies (cached layer if .csproj doesn't change)
RUN dotnet restore "Grimoire/Grimoire.csproj"

# Copy all source code
COPY Channels/Grimoire/ Grimoire/

# Build application in Release mode — InsideDockerBuild skips
# Directory.Build.targets' host-side .env.versions generation, which can't see
# sibling projects here.
WORKDIR "/src/Grimoire"
RUN dotnet build "Grimoire.csproj" -c Release -o /app/build /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish
RUN dotnet publish "Grimoire.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Grimoire"
LABEL org.opencontainers.image.description="A rich-TTY reference channel for the Morgana conversational AI framework — the textual sibling of Cauldron, rendering streaming, rich cards, quick replies and markdown as Spectre.Console ANSI primitives over the webhook delivery mode"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.authors="Marco De Salvo"
LABEL org.opencontainers.image.url="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.source="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.licenses="Apache-2.0"

# Expose port 5004 for HTTP (webhook callback listener)
EXPOSE 5004

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5004
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Grimoire.dll"]
