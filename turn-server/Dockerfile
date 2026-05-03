FROM coturn/coturn:latest

USER root
COPY turnserver.conf /etc/turnserver.conf
COPY --chmod=755 start.sh /start.sh

ENV PORT=3478

EXPOSE 3478/tcp

CMD ["/start.sh"]
