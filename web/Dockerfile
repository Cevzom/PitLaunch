# Static site, served by Caddy.
#
# Caddy rather than Node: there is no server-side logic here, and this keeps the image tiny,
# handles compression and caching headers for us, and has nothing to keep patched.
FROM caddy:2-alpine

COPY Caddyfile /etc/caddy/Caddyfile
COPY public /srv

# Railway injects PORT; the Caddyfile reads it, defaulting to 8080 for local runs.
EXPOSE 8080
