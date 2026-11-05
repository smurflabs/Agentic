# syntax=docker/dockerfile:1
# The Agentic server as a Linux container: one self-contained binary (linux-x64). Its configuration, database, files and
# logs live in /data, which is kept in a volume.
#
#   docker build -t agentic .
#   docker run -d -p 8080:8080 -v agentic-data:/data \
#     -e AGENTIC_AIProviders__OpenWebUI__BaseUrl=https://openwebui.example.com/api \
#     -e AGENTIC_AIProviders__OpenWebUI__ApiKey=sk-... agentic
#
# docker-compose.yml does the same with an .env file; SETUP.md, section 11, explains the settings.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Agentic/Agentic.vbproj src/Agentic/
RUN dotnet restore src/Agentic/Agentic.vbproj -r linux-x64
COPY src/Agentic/ src/Agentic/
RUN dotnet publish src/Agentic/Agentic.vbproj -c Release -r linux-x64 --no-restore -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
# git for the git tool server; curl for the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/Agentic /app/Agentic
COPY --from=build /out/appsettings.json /app/appsettings.default.json
COPY --chmod=0755 docker/entrypoint.sh /app/entrypoint.sh
RUN mkdir -p /data && chown app:app /data

# Listen on every address inside the container; publish the port with -p or in docker-compose.yml.
ENV AGENTIC_Server__Host=* \
    AGENTIC_Server__Port=8080 \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/.net

USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
  CMD curl -fsS "http://127.0.0.1:${AGENTIC_Server__Port:-8080}/healthz" || exit 1
ENTRYPOINT ["/app/entrypoint.sh"]
