# SteamViewer TURN Server

Self-hosted coturn TURN server for NAT traversal, deployed on Railway (TCP only).

## Credentials

Credentials are in `turnserver.conf` (baked into Docker image at deploy time).
They are **not** stored in the app binary - the app fetches them at runtime from the signaling server's `/api/turn-config` endpoint.

To rotate credentials:
1. Update `turnserver.conf` with new `user=<username>:<password>`
2. Redeploy: `railway up`
3. Update signaling server env vars: `TURN_USERNAME` and `TURN_CREDENTIAL`

## Deploy to Railway

1. **Link and deploy:**
   ```bash
   cd turn-server
   railway link
   railway up
   ```

2. **Get your Railway URL:**
   - Go to Railway dashboard -> Settings -> Networking
   - Generate a public domain (e.g., `steamviewer-turn-production.up.railway.app`)

3. **Set signaling server env vars:**
   ```bash
   railway link -p SteamViewer-Signaling
   railway variable set TURN_ENABLED=true
   railway variable set TURN_URLS="turn:<your-turn-domain>:443?transport=tcp"
   railway variable set TURN_USERNAME="<username from turnserver.conf>"
   railway variable set TURN_CREDENTIAL="<password from turnserver.conf>"
   ```

4. **Test** - you should see `ICE candidate gathered: RELAY` in logs

## Testing TURN Server

Use https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

- TURN URI: `turn:<your-turn-domain>:443?transport=tcp`
- Username/password: from `turnserver.conf`

If you see "relay" candidates, it's working!
