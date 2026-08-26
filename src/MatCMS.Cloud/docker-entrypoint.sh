#!/bin/sh
# The image runs the app as the non-root "app" user. When the Docker engine socket is
# mounted (opt-in, for cloud-side container updates) it is mode 660 owned by root:<docker>,
# which "app" may not read — the symptom is SocketException(13) "Permission denied" and every
# instance silently staying "remote".
#
# Rather than make the operator pass group_add / DOCKER_GID (which every other socket-mounting
# app — Portainer, Watchtower, Dockge — avoids by simply running as root), we start PID 1 as
# root, read the socket's own group id and drop straight to "app" with THAT gid as the primary
# group via gosu. So the app process is unprivileged AND can read the socket, and passing the
# socket in compose is all that is ever needed. No socket -> plain "app", exactly as before.
set -e

SOCK=/var/run/docker.sock
if [ -S "$SOCK" ]; then
  exec gosu "app:$(stat -c '%g' "$SOCK")" "$@"
fi
exec gosu app "$@"
