# ==============================================================================
# MORGANA - SIGNALR BACKEND DOCKERFILE
# ==============================================================================
# Multi-stage build for optimized image with all required projects:
# - Morgana.Web (API + SignalR Hub)
# - Morgana.AI (AI framework)
# - Morgana.AI.LanguageModels.Anthropic, Morgana.AI.LanguageModels.OpenAI, Morgana.AI.LanguageModels.AzureOpenAI, Morgana.AI.LanguageModels.Ollama, Morgana.AI.LanguageModels.Google (the LLM providers, referenced by Morgana.Web)
# - Morgana.Contracts (zero-dependency wire contracts, referenced by Morgana.AI)
# - Examples (showcase plugins with 4 agents)
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
# host: Morgana.Web/AI/providers/Contracts reference each other as siblings under Morgana/ and
# Examples (which lives outside Morgana/) references ..\Morgana\Morgana.AI.
COPY ["Morgana/Morgana.Web/Morgana.Web.csproj", "Morgana/Morgana.Web/"]
COPY ["Morgana/Morgana.AI/Morgana.AI.csproj", "Morgana/Morgana.AI/"]
COPY ["Morgana/Morgana.AI.LanguageModels/Anthropic/Morgana.AI.LanguageModels.Anthropic.csproj", "Morgana/Morgana.AI.LanguageModels/Anthropic/"]
COPY ["Morgana/Morgana.AI.LanguageModels/OpenAI/Morgana.AI.LanguageModels.OpenAI.csproj", "Morgana/Morgana.AI.LanguageModels/OpenAI/"]
COPY ["Morgana/Morgana.AI.LanguageModels/AzureOpenAI/Morgana.AI.LanguageModels.AzureOpenAI.csproj", "Morgana/Morgana.AI.LanguageModels/AzureOpenAI/"]
COPY ["Morgana/Morgana.AI.LanguageModels/Ollama/Morgana.AI.LanguageModels.Ollama.csproj", "Morgana/Morgana.AI.LanguageModels/Ollama/"]
COPY ["Morgana/Morgana.AI.LanguageModels/Google/Morgana.AI.LanguageModels.Google.csproj", "Morgana/Morgana.AI.LanguageModels/Google/"]
COPY ["Morgana/Morgana.Contracts/Morgana.Contracts.csproj", "Morgana/Morgana.Contracts/"]
COPY ["Examples/Examples.csproj", "Examples/"]
COPY ["Morgana/Directory.Build.props", "Morgana/"]

# Restore NuGet dependencies (host app + the separately-published plugin project)
RUN dotnet restore "Morgana/Morgana.Web/Morgana.Web.csproj" && \
    dotnet restore "Examples/Examples.csproj"

# Copy all source code from all projects
COPY Morgana/Morgana.Web/ Morgana/Morgana.Web/
COPY Morgana/Morgana.AI/ Morgana/Morgana.AI/
COPY Morgana/Morgana.AI.LanguageModels/Anthropic/ Morgana/Morgana.AI.LanguageModels/Anthropic/
COPY Morgana/Morgana.AI.LanguageModels/OpenAI/ Morgana/Morgana.AI.LanguageModels/OpenAI/
COPY Morgana/Morgana.AI.LanguageModels/AzureOpenAI/ Morgana/Morgana.AI.LanguageModels/AzureOpenAI/
COPY Morgana/Morgana.AI.LanguageModels/Ollama/ Morgana/Morgana.AI.LanguageModels/Ollama/
COPY Morgana/Morgana.AI.LanguageModels/Google/ Morgana/Morgana.AI.LanguageModels/Google/
COPY Morgana/Morgana.Contracts/ Morgana/Morgana.Contracts/
COPY Examples/ Examples/

# Build main project — InsideDockerBuild skips Directory.Build.targets'
# host-side .env.versions generation, which can't see sibling projects here.
WORKDIR "/src/Morgana/Morgana.Web"
RUN dotnet build "Morgana.Web.csproj" -c Release -o /app/build /p:InsideDockerBuild=true

# ==============================================================================
# STAGE 2: PUBLISH
# ==============================================================================
FROM build AS publish

# Publish Morgana.Web (main application)
WORKDIR "/src/Morgana/Morgana.Web"
RUN dotnet publish "Morgana.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InsideDockerBuild=true

# Publish Examples to plugins/ directory
WORKDIR "/src/Examples"
RUN dotnet publish "Examples.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true

# Publish the LLM providers to plugins/ directory, where the host discovers them
WORKDIR "/src/Morgana/Morgana.AI.LanguageModels/Anthropic"
RUN dotnet publish "Morgana.AI.LanguageModels.Anthropic.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true
WORKDIR "/src/Morgana/Morgana.AI.LanguageModels/OpenAI"
RUN dotnet publish "Morgana.AI.LanguageModels.OpenAI.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true
WORKDIR "/src/Morgana/Morgana.AI.LanguageModels/AzureOpenAI"
RUN dotnet publish "Morgana.AI.LanguageModels.AzureOpenAI.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true
WORKDIR "/src/Morgana/Morgana.AI.LanguageModels/Ollama"
RUN dotnet publish "Morgana.AI.LanguageModels.Ollama.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true
WORKDIR "/src/Morgana/Morgana.AI.LanguageModels/Google"
RUN dotnet publish "Morgana.AI.LanguageModels.Google.csproj" -c Release -o /app/publish/plugins /p:UseAppHost=false /p:InsideDockerBuild=true

# What the host already holds leaves plugins/, whether it ships it or the shared framework does: the host's
# own copy is the one loaded, so a second one beside the plugins could only be an older version waiting to be
# picked by mistake. Its symbols and docs go with it
RUN cd /app/publish/plugins && for assembly in *.dll; do \
      if [ -f "/app/publish/${assembly}" ] || ls /usr/share/dotnet/shared/*/*/"${assembly}" > /dev/null 2>&1; then \
        rm -f "${assembly}" "${assembly%.dll}.pdb" "${assembly%.dll}.xml"; \
      fi; \
    done

# ==============================================================================
# STAGE 3: RUNTIME (FINAL IMAGE)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Re-declare ARG for use in this stage
ARG VERSION=latest

# Metadata labels (OCI standard)
LABEL org.opencontainers.image.title="Morgana"
LABEL org.opencontainers.image.description="A magical witch assistant equipped with an enchanted AI-driven grimoire"
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

# Verify plugins directory exists and contains Examples.dll and the five LLM providers
RUN ls -la /app/plugins/ && \
    test -f /app/plugins/Examples.dll || \
    (echo "ERROR: Examples.dll not found in plugins/" && exit 1)
RUN for provider in Anthropic OpenAI AzureOpenAI Ollama Google; do \
      test -f "/app/plugins/Morgana.AI.LanguageModels.${provider}.dll" || \
      (echo "ERROR: Morgana.AI.LanguageModels.${provider}.dll not found in plugins/" && exit 1); \
    done

# Configure ASP.NET Core environment variables
ENV ASPNETCORE_URLS=http://+:5001
ENV ASPNETCORE_ENVIRONMENT=Production

# Application startup
ENTRYPOINT ["dotnet", "Morgana.Web.dll"]
