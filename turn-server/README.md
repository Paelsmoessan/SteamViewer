# SteamViewer TURN Server

Self-hosted coturn TURN server for NAT traversal.

## Credentials
- **Username**: `steamviewer`
- **Password**: `SteamViewer2026SecureRelay!`

## Deploy to Railway (Recommended)

1. **Create new Railway project:**
   ```bash
   cd turn-server
   railway login
   railway init
   railway up
   ```

2. **Get your Railway URL:**
   - Go to Railway dashboard → your project → Settings → Networking
   - Generate a public domain (e.g., `steamviewer-turn-production.up.railway.app`)
   - Note the port (usually shown as `443` for HTTPS or the mapped port)

3. **Update appsettings.json:**
   ```json
   {
     "TurnServer": {
       "Enabled": true,
       "Urls": [
         "turn:steamviewer-turn-production.up.railway.app:443?transport=tcp"
       ],
       "Username": "steamviewer",
       "Credential": "SteamViewer2026SecureRelay!"
     }
   }
   ```

4. **Test the connection** - you should see `ICE candidate gathered: RELAY` in logs

## Alternative: VPS (Full UDP support)

For best performance with UDP, use a VPS (~$5/month):

```bash
# On Ubuntu VPS
sudo apt update && sudo apt install coturn -y
# Edit /etc/turnserver.conf, add: external-ip=YOUR_PUBLIC_IP
sudo systemctl enable coturn && sudo systemctl start coturn
sudo ufw allow 3478/udp && sudo ufw allow 3478/tcp
```

## Testing TURN Server

Use https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

- TURN URI: `turn:YOUR_SERVER:PORT?transport=tcp`
- Username: `steamviewer`
- Password: `SteamViewer2026SecureRelay!`

If you see "relay" candidates, it's working!
