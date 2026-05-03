# SteamViewer TURN Server

Self-hosted coturn TURN server for NAT traversal, deployed on Railway (TCP only).

## Credentials

Credentials are injected at runtime via `TURN_USER` and `TURN_PASS` environment variables on Railway. They are **never** stored in source code or the app binary. The app fetches them at runtime from the signaling server's `/api/turn-config` endpoint.

To rotate credentials:
1. Generate new credentials
2. Update Railway env vars on the TURN project: `TURN_USER` and `TURN_PASS`
3. Update Railway env vars on the signaling project: `TURN_USERNAME` and `TURN_CREDENTIAL`
4. Redeploy the TURN server: `railway up`

## Deploy to Railway

1. **Set credentials:**
   ```bash
   cd turn-server
   railway link
   railway variable set TURN_USER="<username>"
   railway variable set TURN_PASS="<password>"
   ```

2. **Deploy:**
   ```bash
   railway up
   ```

3. **Get your Railway URL:**
   - Go to Railway dashboard -> Settings -> Networking
   - Generate a public domain (e.g., `steamviewer-turn-production.up.railway.app`)

4. **Set signaling server env vars:**
   ```bash
   railway link -p SteamViewer-Signaling
   railway variable set TURN_ENABLED=true
   railway variable set TURN_URLS="turn:<your-turn-domain>:443?transport=tcp"
   railway variable set TURN_USERNAME="<same as TURN_USER>"
   railway variable set TURN_CREDENTIAL="<same as TURN_PASS>"
   ```

5. **Test** - you should see `ICE candidate gathered: RELAY` in logs

## Testing TURN Server

Use https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

- TURN URI: `turn:<your-turn-domain>:443?transport=tcp`
- Username/password: from Railway env vars

If you see "relay" candidates, it's working!
