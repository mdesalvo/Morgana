# ==============================================================================
# ALEMBIC - BLAZOR SERVER DOCKERFILE
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
# mirrored under /src so Alembic's ProjectReference to ../Morgana/Morgana.AI resolves,
# and each project picks up its own Directory.Build.props (Alembic's vs the Morgana one
# that Morgana.AI and Morgana.Contracts inherit).
COPY ["Alembic/Alembic.csproj", "Alembic/"]
COPY ["Alembic/Directory.Build.props", "Alembic/"]
COPY ["Morgana/Morgana.AI/Morgana.AI.csproj", "Morgana/Morgana.AI/"]
COPY ["Morgana/Morgana.Contracts/Morgana.Contracts.csproj", "Morgana/Morgana.Contracts/"]
COPY ["Morgana/Directory.Build.props", "Morgana/"]

# Restore NuGet dependencies (cached layer if .csproj files don't change)
RUN dotnet restore "Alembic/Alembic.csproj"

# Copy all source code (the workbench + the referenced framework projects)
COPY Alembic/ Alembic/
COPY Morgana/Morgana.AI/ Morgana/Morgana.AI/
COPY Morgana/Morgana.Contracts/ Morgana/Morgana.Contracts/

# Nothing of PromptHarness is copied, and nothing needs to be. Alembic's harness component is its
# own — the behavioural templates under Alembic/Harness/Templates, embedded — so the repo layout
# stopped being load-bearing for this build when they replaced the linked scenario schema.

# Build application in Release mode — InsideDockerBuild skips
# Directory.Build.targets' host-side .env.versions generation, which can't see
# sibling projects here.
WORKDIR "/src/Alembic"
RUN dotnet build "Alembic.csproj" -c Release -o /app/build /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish
RUN dotnet publish "Alembic.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Alembic"
LABEL org.opencontainers.image.description="The authoring workbench that distils a domain interview into a Morgana agent"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.authors="Marco De Salvo"
LABEL org.opencontainers.image.url="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.source="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.licenses="Apache-2.0"

# Expose port 5005 for HTTP
EXPOSE 5005

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5005
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Alembic.dll"]
