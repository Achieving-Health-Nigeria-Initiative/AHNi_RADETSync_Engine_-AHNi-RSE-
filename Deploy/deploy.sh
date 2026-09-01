#!/bin/bash
# deploy.sh — runs on MON-01 after binary is uploaded
set -e

DEPLOY_DIR="/tmp/ahni-rse-deploy"
INSTALL_DIR="/opt/ahni-rse"
SERVICE_NAME="ahni-rse"

echo "=== Stopping existing service (if running) ==="
sudo systemctl stop $SERVICE_NAME 2>/dev/null || true

echo "=== Installing files ==="
sudo mkdir -p $INSTALL_DIR
sudo cp $DEPLOY_DIR/AHNiRSE              $INSTALL_DIR/
sudo cp $DEPLOY_DIR/appsettings.json     $INSTALL_DIR/
sudo chmod +x $INSTALL_DIR/AHNiRSE

# Copy Production secrets if provided (never committed to git)
if [ -f "$DEPLOY_DIR/appsettings.Production.json" ]; then
    sudo cp $DEPLOY_DIR/appsettings.Production.json $INSTALL_DIR/
    sudo chmod 600 $INSTALL_DIR/appsettings.Production.json
    echo "  Production config installed."
fi

echo "=== Installing systemd unit ==="
sudo cp $DEPLOY_DIR/ahni-rse.service /etc/systemd/system/
sudo systemctl daemon-reload

echo "=== Starting service ==="
sudo systemctl enable $SERVICE_NAME
sudo systemctl start  $SERVICE_NAME
sudo systemctl status $SERVICE_NAME --no-pager

echo "=== Cleaning up ==="
rm -rf $DEPLOY_DIR

echo "=== Deploy complete ==="
