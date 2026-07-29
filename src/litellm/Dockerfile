# LiteLLM AI gateway.
#
# Build from the repository root so the context matches the other services:
#   docker build -f src/litellm/Dockerfile -t banking-agent-litellm:test .
FROM ghcr.io/berriai/litellm:main-stable

COPY src/litellm/config.yaml /app/config.yaml

# The upstream LiteLLM image runs as root and does not ship a dedicated
# unprivileged user. The constitution requires non-root containers, so we drop
# to an arbitrary high UID. The proxy binds port 4000 (>1024) and, with no
# DATABASE_URL configured, does not need to write anywhere except its cache --
# which is redirected to a writable path below.
ENV HOME=/tmp
ENV XDG_CACHE_HOME=/tmp/.cache
USER 1000

EXPOSE 4000

CMD ["--config", "/app/config.yaml", "--port", "4000"]
