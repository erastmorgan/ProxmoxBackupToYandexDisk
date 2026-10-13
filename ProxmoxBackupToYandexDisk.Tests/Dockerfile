FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["ProxmoxBackupToYandexDisk.Tests/ProxmoxBackupToYandexDisk.Tests.csproj", "ProxmoxBackupToYandexDisk.Tests/"]
COPY ["ProxmoxBackupToYandexDisk.Core/ProxmoxBackupToYandexDisk.Core.csproj", "ProxmoxBackupToYandexDisk.Core/"]

# Restore dependencies
RUN dotnet restore "ProxmoxBackupToYandexDisk.Tests/ProxmoxBackupToYandexDisk.Tests.csproj"

# Copy source code
COPY . .

WORKDIR "/src/ProxmoxBackupToYandexDisk.Tests"
ENTRYPOINT ["dotnet", "test", "--no-restore", "--logger:console;verbosity=normal"]
