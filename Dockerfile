# syntax=docker/dockerfile:1.6
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/edge/PulseBoard.fsproj -c Release -o /out \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl: container health checks (aspnet image is Debian slim and ships
# without it). git: GitOps dashboard/rule syncer (GitSync.fs shells out
# to the `git` CLI for clone/fetch).
RUN apt-get update && apt-get install -y --no-install-recommends curl git \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV PULSE_DATA_DIR=/data
# Bind to all interfaces inside the container so platform health checks
# and public IPs can actually reach the listener. We list BOTH the IPv6
# wildcard (`::`) and the IPv4 wildcard (`0.0.0.0`) because .NET on
# Linux creates AF_INET6 sockets with IPV6_V6ONLY=1 by default — so a
# `::`-only listener silently rejects IPv4 traffic (including a local
# 127.0.0.1 probe). Binding both addresses gives a true dual-stack
# listener so both IPv6 ingress and the IPv4 health check reach Suave.
# Outside the container the default stays loopback so an OSS user
# running `dotnet run` isn't surprised by a LAN-visible socket.
ENV PULSE_BIND_ADDR=::,0.0.0.0
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.dll"]



