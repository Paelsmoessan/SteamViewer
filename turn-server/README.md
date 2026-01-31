# SteamViewer TURN Server

Self-hosted coturn TURN server for NAT traversal.

## Credentials
- **Username**: `steamviewer`
- **Password**: `SteamViewer2026SecureRelay!`

## Deployment Options

### Option 1: VPS (Recommended)
Best performance with full UDP support. ~$5/month from Hetzner, Vultr, or DigitalOcean.

```bash
# On Ubuntu VPS
sudo apt update
sudo apt install coturn -y

# Copy turnserver.conf to /etc/turnserver.conf
# Edit to set your public IP:
#   external-ip=YOUR_PUBLIC_IP

# Enable and start
sudo systemctl enable coturn
sudo systemctl start coturn

# Open firewall
sudo ufw allow 3478/udp
sudo ufw allow 3478/tcp
sudo ufw allow 5349/tcp
sudo ufw allow 49152:49252/udp
```

### Option 2: Docker (Local/VPS)
```bash
cd turn-server
docker-compose up -d
```

### Option 3: Railway (TCP only)
Railway doesn't support UDP well. TURN over TCP works but has higher latency.

```bash
cd turn-server
railway up
```

## Testing TURN Server
Use https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

Enter:
- TURN URI: `turn:YOUR_SERVER:3478`
- Username: `steamviewer`
- Password: `SteamViewer2026SecureRelay!`

If you see "relay" candidates, it's working!

## Update App Config
After deploying, update `webrtc-interop.js` with your server URL.
