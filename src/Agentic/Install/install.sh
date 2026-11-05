#!/usr/bin/env bash
# Agentic CLI installer for Linux and macOS:   curl -fsSL __SERVER__/install.sh | bash
#
# Installs agentic into ~/.local/share/agentic (it updates itself there), links ~/.local/bin/agentic, points it at this
# server and adds ~/.local/bin to PATH in your shell's startup files when it is missing.
# Environment options: AGENTIC_INSTALL_DIR, AGENTIC_BIN_DIR = other folders; AGENTIC_NO_MODIFY_PATH=1 leaves shell files alone.
set -euo pipefail
server='__SERVER__'

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) echo "Agentic CLI: $(uname -s) is not supported here. On Windows run: irm $server/install.ps1 | iex" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) echo "Agentic CLI: the $(uname -m) processor is not supported." >&2; exit 1 ;;
esac
channel="cli-$os-$arch"
version=''
sha=''
case "$channel" in
__BUILDS__
  *) ;;
esac
if [ -z "$sha" ]; then echo "Agentic CLI: this server has no build for $os-$arch yet." >&2; exit 1; fi

dir="${AGENTIC_INSTALL_DIR:-$HOME/.local/share/agentic}"
bin="${AGENTIC_BIN_DIR:-$HOME/.local/bin}"
mkdir -p "$dir" "$bin"
echo "Installing Agentic CLI $version from $server ..."
tmp="$dir/agentic.download"
if command -v curl >/dev/null 2>&1; then
  curl -fsSL "$server/install/bin/$channel" -o "$tmp"
elif command -v wget >/dev/null 2>&1; then
  wget -qO "$tmp" "$server/install/bin/$channel"
else
  echo "Agentic CLI: curl or wget is needed." >&2; exit 1
fi
if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$tmp" | cut -d' ' -f1); else actual=$(shasum -a 256 "$tmp" | cut -d' ' -f1); fi
if [ "$actual" != "$sha" ]; then
  rm -f "$tmp"
  echo "Agentic CLI: the download did not match its SHA-256; nothing was installed." >&2; exit 1
fi
chmod +x "$tmp"
mv -f "$tmp" "$dir/agentic"
# macOS quarantines downloaded binaries; this one was checked above.
if [ "$os" = osx ]; then xattr -d com.apple.quarantine "$dir/agentic" 2>/dev/null || true; fi
ln -sf "$dir/agentic" "$bin/agentic"
# Point it at this server, unless it already has a config (the key from an earlier install stays).
[ -f "$dir/config.json" ] || printf '{ "serverBaseUrl": "%s" }\n' "$server" > "$dir/config.json"

case ":$PATH:" in
  *":$bin:"*) ;;
  *)
    line="export PATH=\"$bin:\$PATH\""
    if [ "${AGENTIC_NO_MODIFY_PATH:-}" != 1 ]; then
      shell_name=$(basename "${SHELL:-sh}")
      for rc in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
        case "$rc" in
          */.bashrc) [ -f "$rc" ] || [ "$shell_name" = bash ] || continue ;;
          */.zshrc) [ -f "$rc" ] || [ "$shell_name" = zsh ] || continue ;;
        esac
        grep -qsF "$line" "$rc" || printf '\n# Agentic CLI\n%s\n' "$line" >> "$rc"
      done
      echo "Added $bin to PATH in your shell startup files. Open a new terminal, or run: $line"
    else
      echo "Add $bin to your PATH: $line"
    fi
    ;;
esac

echo
echo "Agentic CLI $version installed: $bin/agentic"
echo "Next: agentic login   (sign in with an API key from Account in the web app), then run agentic in a project folder."
