#!/bin/bash
#
# Installation script for Proxmox Backup to Yandex Disk
# For Debian 12 (bookworm) and compatible systems
#

set -e

INSTALL_DIR="/opt/proxmox-backup"
SERVICE_NAME="proxmox-backup"
DOTNET_VERSION="10.0"

echo "=========================================="
echo "Proxmox Backup to Yandex Disk Installer"
echo "=========================================="
echo

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo "Please run as root (sudo ./install.sh)"
    exit 1
fi

# Check for .NET runtime
echo "Checking for .NET runtime..."
if ! command -v dotnet &> /dev/null; then
    echo ".NET runtime not found. Installing..."

    # Install Microsoft package repository
    wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
    dpkg -i packages-microsoft-prod.deb
    rm packages-microsoft-prod.deb

    apt-get update
    apt-get install -y aspnetcore-runtime-${DOTNET_VERSION}

    echo ".NET runtime installed successfully"
else
    echo ".NET runtime found: $(dotnet --version)"
fi

# Create installation directory
echo
echo "Creating installation directory..."
mkdir -p "$INSTALL_DIR"

# Build and publish the application
echo
echo "Building application..."
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "$SCRIPT_DIR/ProxmoxBackupToYandexDisk.sln" ]; then
    cd "$SCRIPT_DIR"
    dotnet publish ProxmoxBackupToYandexDisk.Console -c Release -o "$INSTALL_DIR" --self-contained false
else
    echo "Error: Source code not found. Please run this script from the project directory."
    exit 1
fi

# Set permissions
echo
echo "Setting permissions..."
chmod +x "$INSTALL_DIR/ProxmoxBackupToYandexDisk.Console"
chown -R root:root "$INSTALL_DIR"

# Install systemd service
echo
echo "Installing systemd service..."
cp "$SCRIPT_DIR/proxmox-backup.service" /etc/systemd/system/
systemctl daemon-reload

echo
echo "=========================================="
echo "Installation complete!"
echo "=========================================="
echo
echo "Next steps:"
echo
echo "1. Configure the application (run interactively first):"
echo "   $INSTALL_DIR/ProxmoxBackupToYandexDisk.Console"
echo
echo "2. Set up Proxmox connection, Yandex Disk, and scheduler"
echo
echo "3. Enable and start the service:"
echo "   systemctl enable $SERVICE_NAME"
echo "   systemctl start $SERVICE_NAME"
echo
echo "4. Check service status:"
echo "   systemctl status $SERVICE_NAME"
echo "   journalctl -u $SERVICE_NAME -f"
echo
echo "To uninstall:"
echo "   systemctl stop $SERVICE_NAME"
echo "   systemctl disable $SERVICE_NAME"
echo "   rm /etc/systemd/system/proxmox-backup.service"
echo "   rm -rf $INSTALL_DIR"
echo
