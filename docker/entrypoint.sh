#!/bin/sh
# Starts the Agentic server with its configuration in /data. On the first start the default configuration is copied
# there, where it is kept and can be edited; environment variables (AGENTIC_<Section>__<Key>) override it.
set -e
config="${AGENTIC_CONFIG:-/data/appsettings.json}"
if [ ! -f "$config" ]; then
  cp /app/appsettings.default.json "$config"
  echo "Agentic: wrote the default configuration to $config. Edit it to set your models (SETUP.md, section 4)."
fi
exec /app/Agentic --config "$config" "$@"
