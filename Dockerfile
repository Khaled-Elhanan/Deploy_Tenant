# ==============================================================================
# Tenantix Platform API - Multi-Stage Dockerfile (Render + PostgreSQL)
# ==============================================================================
# Build:   docker build -t tenantix-api .
# Run:     docker run -p 8080:8080 -e ConnectionStrings__DefaultConnection="..." tenantix-api
# ==============================================================================

# --- Stage 1: Base runtime image ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
RUN adduser --disabled-password --gecos "" --home /app appuser
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# --- Stage 2: Build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files first (layer caching for restore)
COPY ["Tenantix Platform/Tenantix.WebApi.csproj", "Tenantix Platform/"]
COPY ["Tenantix.Application/Tenantix.Application.csproj", "Tenantix.Application/"]
COPY ["Tenantix.Domain/Tenantix.Domain.csproj", "Tenantix.Domain/"]
COPY ["Tenantix.Infrastructure/Tenantix.Infrastructure.csproj", "Tenantix.Infrastructure/"]
COPY ["Tenantix.Shared/Tenantix.Shared.csproj", "Tenantix.Shared/"]

# Restore dependencies (cached unless .csproj files change)
RUN dotnet restore "Tenantix Platform/Tenantix.WebApi.csproj"

# Copy everything and build
COPY . .
WORKDIR "/src/Tenantix Platform"
RUN dotnet build "Tenantix.WebApi.csproj" -c $BUILD_CONFIGURATION -o /app/build

# --- Stage 3: Publish ---
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Tenantix.WebApi.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# --- Stage 4: Final runtime image ---
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Switch to non-root user
USER appuser

ENTRYPOINT ["dotnet", "Tenantix.WebApi.dll"]
