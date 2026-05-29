# syntax=docker/dockerfile:1.6
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/edge/PulseBoard.fsproj -c Release -o /out \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Install curl for container health checks (aspnet image is Debian slim
# and ships without it).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV PULSE_DATA_DIR=/data
# Bind to all interfaces inside the container so Fly's health checks and
# flycast / public IPs can actually reach the listener. We list BOTH the
# IPv6 wildcard (`::`) and the IPv4 wildcard (`0.0.0.0`) because .NET on
# Linux creates AF_INET6 sockets with IPV6_V6ONLY=1 by default — so a
# `::`-only listener silently rejects IPv4 traffic (including the local
# 127.0.0.1 probe Fly's health check uses). Binding both addresses gives
# a true dual-stack listener: flycast (IPv6) and the local health check
# (IPv4) both reach Suave. Outside the container the default stays
# loopback so an OSS user running `dotnet run` isn't surprised by a
# LAN-visible socket.
ENV PULSE_BIND_ADDR=::,0.0.0.0
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.dll"]
# argv (--multi-tenant, --role=storage, --role=edge …) is set by the
# workspace deploy that consumes this image.


