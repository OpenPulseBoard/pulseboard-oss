# syntax=docker/dockerfile:1.6
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/cloud/PulseBoard.Cloud.fsproj -c Release -o /out \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV PULSE_BIND_ADDR=::,0.0.0.0
EXPOSE 8080
ENTRYPOINT ["dotnet", "PulseBoard.Cloud.dll"]
# argv (--site-only or --mode=provisioner) is set by each hosted deploy.