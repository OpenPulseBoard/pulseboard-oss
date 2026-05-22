# syntax=docker/dockerfile:1.6
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/edge/PulseBoard.fsproj -c Release -o /out \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV PULSE_DATA_DIR=/data
# Bind to all interfaces inside the container so Fly's health checks and
# flycast / public IPs can actually reach the listener. Outside the
# container the default stays loopback so an OSS user running `dotnet run`
# isn't surprised by a LAN-visible socket.
ENV PULSE_BIND_ADDR=0.0.0.0
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.dll"]
# argv (--site-only, --mode=provisioner, --multi-tenant …) is set by
# each Fly app's [processes].app line.


