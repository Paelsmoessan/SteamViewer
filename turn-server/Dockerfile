FROM coturn/coturn:latest

COPY turnserver.conf /etc/turnserver.conf
COPY start.sh /start.sh
RUN chmod +x /start.sh

ENV PORT=3478

EXPOSE 3478/tcp

CMD ["/start.sh"]
