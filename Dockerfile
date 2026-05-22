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
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.dll"]
# argv (--site-only, --mode=provisioner, --multi-tenant …) is set by
# each Fly app's [processes].app line.


