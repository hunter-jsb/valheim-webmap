#!/usr/bin/env bash
# A throwaway local Valheim server for testing the mod, using the same image the
# host runs (indifferentbroccoli/valheim-server-docker), so what passes here is
# what production does.
#
#   ./testserver.sh up       start it (first run downloads the game, ~10 min)
#   ./testserver.sh deploy   build the mod, drop it in, restart
#   ./testserver.sh logs     follow the server log
#   ./testserver.sh status   container + mod endpoints
#   ./testserver.sh down     stop and remove the container (world is kept)
#   ./testserver.sh nuke     ...and delete the world and downloaded game
#
# Join it from the game with: Join by IP -> 127.0.0.1:2456, password below.
set -euo pipefail

# rootless podman needs this when the shell running the script did not inherit it,
# otherwise exec/cp fail with "configure storage: mkdir /run/containers: permission denied"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

NAME=valheim-test
DATA="${VALHEIM_TEST_DIR:-$HOME/valheim-test}"
IMAGE=docker.io/indifferentbroccoli/valheim-server-docker:latest
PASSWORD=testpass123          # local only; must be 5+ chars and not inside the server name
WEBPORT=3000                  # the mod's own HTTP port (its default)

case "${1:-}" in
up)
  mkdir -p "$DATA/server-files" "$DATA/server-data"
  # If the first boot dies with "Failed to install app '896660' (Missing configuration)",
  # just run this again: steamcmd fails app_update that way in about one run in four,
  # having downloaded nothing. It is transient and not worth working around.
  # No --userns=keep-id: the image sets file permissions as root during init, which
  # it cannot do when our uid is mapped in. Rootless podman maps container root to
  # us on the host instead, so the data dirs end up owned by a subuid -- use
  # `./testserver.sh deploy` (podman cp) rather than writing into them directly.
  podman run -d --name "$NAME" \
    -e PUID=1000 -e PGID=1000 \
    -p 2456-2458:2456-2458/udp \
    -p "$WEBPORT:$WEBPORT/tcp" \
    -v "$DATA/server-files:/valheim:z" \
    -v "$DATA/server-data:/valheim-saves:z" \
    -e SERVER_NAME="WebMap Test" \
    -e WORLD_NAME="WebMapTest" \
    -e SERVER_PASSWORD="$PASSWORD" \
    -e SERVER_PUBLIC=false \
    -e BEPINEX_ENABLED=true \
    "$IMAGE"
  echo "started; first boot downloads the game. ./testserver.sh logs"
  ;;
deploy)
  dotnet build WebMap/WebMap.csproj -c Release -v minimal
  # One podman cp of a staged directory: rootless podman owns the bind mount as a
  # subuid, so the host cannot write it directly, and cp creates the destination.
  STAGE="$(mktemp -d)/WebMap"; mkdir -p "$STAGE"
  cp WebMap/bin/Release/WebMap.dll WebMap/bin/Release/websocket-sharp.dll "$STAGE/"
  cp -r WebMap/web "$STAGE/web"
  podman cp "$STAGE" "$NAME:/valheim/BepInEx/plugins/"
  podman restart "$NAME" >/dev/null
  echo "deployed and restarted"
  ;;
logs)    podman logs -f "$NAME" ;;
status)
  podman ps --filter "name=$NAME" --format '{{.Names}}  {{.Status}}'
  for p in /players /config /map.jpg; do
    printf '  %-10s %s\n' "$p" "$(curl -s -m 5 -o /dev/null -w '%{http_code} %{size_download}b' "http://127.0.0.1:$WEBPORT$p" || echo unreachable)"
  done
  ;;
down)    podman rm -f "$NAME" ;;
nuke)    podman rm -f "$NAME" 2>/dev/null || true; rm -rf "${DATA:?}/server-files" "${DATA:?}/server-data" ;;
*)       sed -n '2,14p' "$0"; exit 1 ;;
esac
