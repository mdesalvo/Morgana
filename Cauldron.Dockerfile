# ==============================================================================
# CAULDRON - BLAZOR SERVER DOCKERFILE
# ==============================================================================
# Multi-stage build to optimize final image size
# Stage 1: Build with full SDK (~1 GB)
# Stage 2: Runtime with ASP.NET Core Runtime only (~200 MB)

# ==============================================================================
# STAGE 1: BUILD
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and dependencies (for optimal layer caching)
COPY ["Cauldron/Cauldron.csproj", "Cauldron/"]
COPY ["Cauldron/Directory.Build.props", "Cauldron/"]

# Restore NuGet dependencies (cached layer if .csproj doesn't change)
RUN dotnet restore "Cauldron/Cauldron.csproj"

# Copy all source code
COPY Cauldron/ Cauldron/

# Build application in Release mode
WORKDIR "/src/Cauldron"
RUN dotnet build "Cauldron.csproj" -c Release -o /app/build

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish
RUN dotnet publish "Cauldron.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Metadata labels
LABEL org.opencontainers.image.title="Cauldron"
LABEL org.opencontainers.image.description="A magical witch assistant equipped with an enchanted AI-driven grimoire (FrontEnd)"
LABEL org.opencontainers.image.version="0.12.0"
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
