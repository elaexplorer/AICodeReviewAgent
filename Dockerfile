# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY CodeReviewAgent.csproj ./
RUN dotnet restore CodeReviewAgent.csproj

# Copy everything else and build
COPY . ./
RUN dotnet publish CodeReviewAgent.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install curl (health checks) and git (RAG clone-based indexing)
RUN apt-get update && apt-get install -y curl git && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Copy static files (wwwroot)
COPY --from=build /src/wwwroot ./wwwroot

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/api/codereview/config/status || exit 1

# Run the application
ENTRYPOINT ["dotnet", "CodeReviewAgent.dll"]
