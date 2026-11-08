# ==============================================================================
# GARDENING MCP - THE HEAD GARDENER'S ALMANAC DOCKERFILE
# ==============================================================================
# Multi-stage build for the MCP server of the Examples plugin with all required projects:
# - GardeningMCP (the MCP server serving the head gardener's almanac)
# - Examples (the plugin whose database the server reads)
# - Morgana.AI (AI framework, referenced by Examples)
# - Morgana.Contracts (zero-dependency wire contracts, referenced by Morgana.AI)
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

# Copy project files for all required projects (layer caching optimization). The repo
# layout is mirrored under /src so every ProjectReference resolves identically to the
# host: GardeningMCP references ..\Examples.csproj and Examples (which lives outside Morgana/)
# references ..\Morgana\Morgana.AI.
COPY ["Morgana/Morgana.AI/Morgana.AI.csproj", "Morgana/Morgana.AI/"]
COPY ["Morgana/Morgana.Contracts/Morgana.Contracts.csproj", "Morgana/Morgana.Contracts/"]
COPY ["Examples/Examples.csproj", "Examples/"]
COPY ["Examples/GardeningMCP/GardeningMCP.csproj", "Examples/GardeningMCP/"]
COPY ["Morgana/Directory.Build.props", "Morgana/"]

# Restore NuGet dependencies (the server and every project it references)
RUN dotnet restore "Examples/GardeningMCP/GardeningMCP.csproj"

# Copy all source code from all projects (Examples/ covers both the plugin and the server)
COPY Morgana/Morgana.AI/ Morgana/Morgana.AI/
COPY Morgana/Morgana.Contracts/ Morgana/Morgana.Contracts/
COPY Examples/ Examples/

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish

# Publish GardeningMCP (InsideDockerBuild skips Directory.Build.targets'
# host-side .env.versions generation, which can't see sibling projects here)
WORKDIR "/src/Examples/GardeningMCP"
RUN dotnet publish "GardeningMCP.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Morgana Gardening MCP"
LABEL org.opencontainers.image.description="The MCP server of the Examples plugin, serving the head gardener's almanac of The Greenhouse & Nursery"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.authors="Marco De Salvo"
LABEL org.opencontainers.image.url="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.source="https://github.com/mdesalvo/Morgana"
LABEL org.opencontainers.image.licenses="Apache-2.0"

# No port is exposed: the server is reached only from inside Morgana's network namespace, at the address that GardeningAgent names

# Copy compiled binaries from publish stage
COPY --from=publish /app/publish .

# Create the directory where the plugin's database is read from, the volume that Morgana writes it to
RUN mkdir -p /app/data

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "GardeningMCP.dll"]
