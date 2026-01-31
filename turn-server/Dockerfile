# Coturn TURN server for SteamViewer.NET
FROM coturn/coturn:latest

# Copy configuration
COPY turnserver.conf /etc/turnserver.conf

# Expose TURN ports
# 3478 - STUN/TURN UDP and TCP
# 5349 - TURN TLS
# 49152-65535 - Relay ports (Railway only exposes main port, so we use fewer)
EXPOSE 3478/udp
EXPOSE 3478/tcp
EXPOSE 5349/tcp

# Run coturn with our config
CMD ["turnserver", "-c", "/etc/turnserver.conf"]
