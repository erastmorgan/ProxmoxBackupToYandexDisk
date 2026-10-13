FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files
COPY ["ProxmoxBackupToYandexDisk.Console/ProxmoxBackupToYandexDisk.Console.csproj", "ProxmoxBackupToYandexDisk.Console/"]
COPY ["ProxmoxBackupToYandexDisk.Core/ProxmoxBackupToYandexDisk.Core.csproj", "ProxmoxBackupToYandexDisk.Core/"]

# Restore dependencies
RUN dotnet restore "ProxmoxBackupToYandexDisk.Console/ProxmoxBackupToYandexDisk.Console.csproj"

# Copy source code
COPY . .

# Build
WORKDIR "/src/ProxmoxBackupToYandexDisk.Console"
RUN dotnet build "./ProxmoxBackupToYandexDisk.Console.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ProxmoxBackupToYandexDisk.Console.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app

# Create config directory
RUN mkdir -p /root/.proxmox-backup

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ProxmoxBackupToYandexDisk.Console.dll"]
