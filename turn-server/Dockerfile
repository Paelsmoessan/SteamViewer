# Coturn TURN server for SteamViewer.NET
# Optimized for Railway deployment (TCP only)
FROM coturn/coturn:latest

# Copy configuration
COPY turnserver.conf /etc/turnserver.conf

# Railway provides PORT env var, default to 3478
ENV PORT=3478

# Expose TCP port (Railway only supports TCP)
EXPOSE 3478/tcp

# Start script that uses Railway's PORT
CMD turnserver -c /etc/turnserver.conf --listening-port=${PORT} --no-udp --no-dtls
