#!/bin/sh
if [ -z "$TURN_USER" ] || [ -z "$TURN_PASS" ]; then
    echo "ERROR: TURN_USER and TURN_PASS env vars required"
    exit 1
fi

echo "user=${TURN_USER}:${TURN_PASS}" >> /etc/turnserver.conf
exec turnserver -c /etc/turnserver.conf --listening-port=${PORT:-3478} --no-udp --no-dtls
