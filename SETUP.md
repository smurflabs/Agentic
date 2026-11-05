# Setting up the Agentic server

This guide takes you from a fresh clone to a running server. Then it covers the optional parts: tools, services, a Windows Service and publishing the client apps. The [reference manual](docs/REFERENCE.md) explains every feature and setting in depth.

**Docker:** to run the published image on Linux instead of building on Windows, go straight to [section 11](#11-docker).

The client apps connect to this server:

| Repository | What it is |
|---|---|
| **Agentic** (this one) | The server: agents, the web app (Chat, dashboards), the API |
| **Agentic_cli** | A coding agent in your terminal |
| **Agentic_desktop** | The desktop app (Windows, Linux) |
| **Agentic_vscode** | The VS Code extension |

## 1. What you need

- **Windows 10/11 or Windows Server 2019 or later, x64.** The server, its service mode and the scripts in `tools\` are built and tested on Windows. On Linux, use the Docker image (section 11).
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** to build. The published `Agentic.exe` is self-contained, so the machine that only runs it does not need .NET.
- **A model provider.** The server talks to an OpenAI-compatible API (`/chat/completions`, `/models`). The shipped entry is OpenWebUI (`AIProviders.OpenWebUI`), which can sit in front of llama.cpp, Ollama, vLLM or cloud providers. You need its base URL and an API key.
- **Optional extras** (section 6): Node.js 20 or later and Google Chrome for the browser, diagram and reverse-engineering tools. Ghidra for reverse engineering. A Gitea instance. Self-hosted speech, image and music servers.

## 2. Build and test

```powershell
git clone <this repository> Agentic
cd Agentic
dotnet build Agentic.slnx
dotnet test  Agentic.slnx      # optional; the full suite takes several minutes
```

## 3. Publish a build to run

```powershell
dotnet publish src\Agentic\Agentic.vbproj -c Release -r win-x64 -o C:\Agentic
```

This creates `C:\Agentic\Agentic.exe` (one self-contained file) with `appsettings.json` next to it. The server keeps its data next to the config file:

| Path | What |
|---|---|
| `data\` | The SQLite database, artifacts, uploads, backups |
| `logs\` | JSON-lines logs, secrets redacted |

> **Upgrading later:** publishing again into the same folder can replace your edited `appsettings.json`. Publish into a staging folder and copy only `Agentic.exe`, or use `tools\publish-server.ps1` (section 8). That script never writes the live config.

## 4. Configure

Edit `C:\Agentic\appsettings.json`. Comments are allowed in this file.

**Never put secrets in this file.** Every setting can be set by an environment variable instead: `AGENTIC_<Section>__<Key>`, for example `AGENTIC_AIProviders__OpenWebUI__ApiKey`. Keys that you enter on the web app's Settings pages are stored encrypted in the server's vault.

### 4.1 Model provider

```jsonc
"AIProviders": {
  "OpenWebUI": {
    "BaseUrl": "https://openwebui.example.com/api",   // your provider's OpenAI-compatible base URL
    "ApiKey": ""                                       // leave empty; use the environment variable below
  }
}
```

```powershell
setx /M AGENTIC_AIProviders__OpenWebUI__ApiKey "sk-..."   # machine-wide; open a new console afterwards
```

Use a dedicated, non-admin account on the provider for this key.

### 4.2 Models

`Models[]` lists the models that agents and Chat can use. The shipped list names the original author's models, so change each entry's `ModelId` to a model your provider serves. `GET <BaseUrl>/models` on the provider lists them.

- Keep the `Key` values (`local-main`, `coding-t2`, `vision`, ...). Agents, fallbacks and routing refer to models by key.
- `IsLocal: true` marks a free local model on your own server (section 4.3). Agents run on it by default.
- **Cloud models are never used until they have a price** (fail-closed). Set the prices in `Models[].Pricing`, or after the first start under **Finance → Model pricing**.

Then check the configuration and every model:

```powershell
C:\Agentic\Agentic.exe --validate-config
C:\Agentic\Agentic.exe --probe-models      # chat, tool calls and vision for every model
```

`--validate-config` names each problem and exits with code 1 when the config is invalid. Missing secrets for optional services (section 6) show as warnings only.

### 4.3 Local models

`local-main` and `local-vision` are your own model servers (llama.cpp or similar), free for agents. They are optional:

```jsonc
"LocalModels": {
  "Enabled": true,       // false: this server has no local models
  "Slots": 4,            // requests each local model server works on at once; 0 = each model's MaxConcurrency
  "SlotsByModel": {}     // slots for one model, e.g. { "local-vision": 2 }
}
```

- **Slots:** set `Slots` to the number of requests your servers handle in parallel (llama.cpp's `--parallel`). Use `SlotsByModel` when the two servers differ.
- **No local models:** set `"Enabled": false`, or the environment variable `AGENTIC_LocalModels__Enabled=false`. Then:
  - `local-main` and `local-vision` are dropped, with every reference to them: routing ladders, spillover, fallbacks and agents' model lists.
  - Agents that only had local models use the first model of the `Default` ladder.
  - Global tasks are off, because their planner runs only on local models.
  - Learning studies use rules only.
  - Agents run on cloud models, which need prices first (section 4.2).
- **Switching off later:** agents keep the model list they were saved with, but a model that no longer exists is skipped. The shipped agents all have cloud models as well. To change an agent's models, edit the agent in the web app.

### 4.4 Web server

```jsonc
"Server": {
  "Host": "127.0.0.1",      // "*" or "+" for every address (needs a URL ACL, see below)
  "Port": 8080,
  "PublicUrl": ""           // the address people use, e.g. https://agentic.example.com (used for share links)
}
```

Other machines can reach the server through an HTTPS reverse proxy (Caddy, nginx, IIS ARR) in front of `127.0.0.1:8080`. Do not expose the server directly to the internet. To bind other addresses yourself:

```powershell
netsh http add urlacl url=http://+:8080/ user=DOMAIN\your-service-account
```

### 4.5 MCP servers that need extra software

Three shipped `McpServers[]` entries are **off** until you set up what they need. Then set `"Enabled": true`:

| Id | Needs |
|---|---|
| `code-exec` | An Open Terminal MCP server (`Endpoint`) plus `MCP_CODE_EXEC_TOKEN` |
| `browser` | Node.js, `tools\playwright-mcp` (section 6), Chrome |
| `rea` | Node.js, `tools\rea` (section 6), Ghidra |

The other built-in servers need nothing extra. A server that cannot connect only removes its own tools; everything else keeps working.

## 5. First start

```powershell
C:\Agentic\Agentic.exe
```

1. With no users in `Admin.Users`, the console prints a **one-time password** for the user `admin`.
2. Open `http://127.0.0.1:8080` and sign in as `admin`.
3. Make a permanent administrator. Run `C:\Agentic\Agentic.exe --hash-password` and add the hash to the config:

   ```jsonc
   "Admin": { "Users": [ { "Username": "you", "PasswordHash": "<the printed hash>" } ] }
   ```

   You can also create accounts in the web app under **Users & access**.
4. Set model prices (**Finance → Model pricing**) for every cloud model that agents and users may use.
5. For each person who will use a client app:
   - Under **Users & access**, give their account **Chat & model API**. Add **Chat tools & code** if they should run server tools and code from Chat.
   - They then create an API key in the user menu → **API key** → **Generate**.

### Run as a Windows Service

```powershell
sc.exe create Agentic binPath= "C:\Agentic\Agentic.exe --service --config C:\Agentic\appsettings.json" start= delayed-auto
sc.exe failure Agentic reset= 86400 actions= restart/60000/restart/60000/restart/300000
sc.exe start Agentic
```

A service has no console, so it cannot show the bootstrap password. Add your `Admin.Users` entry before you install it. Set the environment variables machine-wide (`setx /M`), because the service reads the machine environment.

## 6. Optional tools and services

### Node.js tools (browser, diagrams, reverse engineering)

The browser, Mermaid diagrams and `rea` run on Node.js 20 or later. Install Node, or unzip a portable copy into `tools\node\` (git-ignored). Then install the packages:

```powershell
cd tools\playwright-mcp; npm install; cd ..\..
cd tools\mermaid-cli;    npm install; cd ..\..
cd tools\rea;            npm install; cd ..\..
```

The shipped config refers to these files through the placeholder folder `C:\Agentic\tools\`. Change each path to where the files really are:

| Config entry | Paths to set |
|---|---|
| `McpServers` → `browser` | `Command`: `node.exe`. First argument: `tools\playwright-mcp\node_modules\@playwright\mcp\cli.js` (Chrome must be installed) |
| `McpServers` → `rea` | `Command`: `node.exe`. First argument: `tools\rea\node_modules\rea-agents\scripts\rea.mjs`. `GHIDRA_INSTALL_DIR`: your Ghidra folder |
| `McpServers` → `data` | `--node`: `node.exe`. `--mermaid-cli`: `tools\mermaid-cli\node_modules\@mermaid-js\mermaid-cli\src\cli.js` |

In a stdio server's `Command`, `Arguments` and `WorkingDirectory`:
- `{self}` is replaced by the path of `Agentic.exe`.
- `{data}` is replaced by the data folder.

### Gitea

1. Download `gitea-mcp.exe` from the [gitea-mcp releases](https://gitea.com/gitea/gitea-mcp/releases) into `tools\gitea-mcp\` (git-ignored).
2. In the `gitea` entry:
   - Set the `--gitea-mcp` argument to the path of that file.
   - Set `GITEA_HOST` to your Gitea's address.
   - Set `"Enabled": true`.
3. Set `GITEA_ACCESS_TOKEN` (a Gitea access token) as an environment variable.
4. The `git` server uses the same host and token.

### Other services

Each one is off, or only partly works, until you configure it:

| Feature | Settings | Secret |
|---|---|---|
| Image and video generation in Chat | `ImageGeneration` / `VideoGeneration`: `BaseUrl`, `Model` | `AGENTIC_ImageGeneration__ApiKey`, `AGENTIC_VideoGeneration__ApiKey` |
| Speech (Read aloud, Dictation, Audio tab) | `TextToSpeech.BaseUrl`: an XTTS-v2 server | Audio → Settings, or `AGENTIC_TextToSpeech__ApiKey` |
| Images tab | `StableDiffusion.BaseUrl`: a Stable Diffusion 1.5 API server | Images → Settings, or `AGENTIC_StableDiffusion__ApiKey` |
| Music tab | `Music.BaseUrl`: a Suno API wrapper | Music → Settings, or `AGENTIC_Music__ApiKey` |
| Web search | `WebSearch`: Brave (key), Bing (no key), SearXNG (`UrlTemplate`) | `AGENTIC_WebSearch__BraveApiKey` |
| Knowledge bases (RAG) | `Rag.EmbeddingModel`: an embedding model on your provider | (none) |
| Deposits (BTCPay) | `Payments`: `BtcPayUrl`, `StoreId` | `AGENTIC_Payments__ApiKey` |
| Email for agents | `McpServers` → `email`, `Notifications.Email` | `EMAIL_PASSWORD`, `AGENTIC_SMTP_PASSWORD` |
| Docker for agents | `McpServers` → `docker`: `DOCKER_HOSTS` | (none) |
| SSH for agents (`netops`) | `SSH_HOSTS` | `SSH_KEY_<NAME>` or `SSH_PASSWORD_<NAME>` |
| Crypto prices (Money tab) | `Markets` | `AGENTIC_Markets__CryptoApiKey` (optional; a free fallback is used) |

## 7. Monitoring and backups

- **Health:** `GET /healthz` answers 200 while the server runs.
- **Metrics:** `GET /api/metrics` serves Prometheus text format. It needs an API token with the `read` scope (`Admin.ApiTokens`).
- **Backups:** the database is backed up every `Retention.BackupIntervalHours` into `data\backups`, and before every schema migration. To restore one, stop the server and copy the backup over `data\agentic.db`.

## 8. Upgrades with `tools\publish-server.ps1`

The script keeps a live install in `Agentic\publish\`, which is git-ignored. Set it up once:

```powershell
mkdir publish
copy src\Agentic\appsettings.json publish\appsettings.json   # then edit it as in section 4
```

Each run of the script does the following, in order:
1. Picks the next version and writes it into `src\Agentic\Agentic.vbproj`. That is the project's version when it is higher than the live one, otherwise the live version plus one patch.
2. Builds `Agentic.exe` and checks `publish\appsettings.json` with the new build.
3. Stops the running server (only processes started from `publish\Agentic.exe`).
4. Backs up the old exe and the database, and copies the new exe in.
5. Starts the server and watches it until `/healthz` answers.
6. Writes a changelog entry, which the web app shows under **Apps & updates**.

```powershell
.\tools\publish-server.ps1 -Title "My first release" -Features "..." -Fixes "..."
.\tools\publish-server.ps1 -DryRun              # show the version it would publish
.\tools\publish-server.ps1 -Rollback <tag>      # put the previous exe back (the tag is printed by each run)
.\tools\publish-server.ps1 -Port 9000           # when Server.Port is not 8080
```

The script never writes `publish\appsettings.json`.

## 9. Offering the client apps from your server

The server can hand out the CLI, the desktop app and the VS Code extension. Installed copies update themselves from it.

1. Clone the client repositories next to this one:

   ```
   <folder>\Agentic
   <folder>\Agentic_cli
   <folder>\Agentic_desktop
   <folder>\Agentic_vscode
   ```

2. Install Node.js 20 or later for the VS Code package. `tools\node\node.exe` is used when it exists, otherwise `node` on `PATH`.
3. Run the script from a PowerShell prompt, so that `-Apps cli,desktop` reaches it as a list:

   ```powershell
   .\tools\publish-clients.ps1                                   # all apps, Windows and Linux, into publish\updates
   .\tools\publish-clients.ps1 -Apps cli -Notes "Faster startup"
   .\tools\publish-clients.ps1 -Apps desktop -Platforms linux-x64
   ```

   Running it with `powershell -File ...` passes `cli,desktop` as one string, and the `-Apps` check rejects it.
4. Raise `<Version>` in a client's project (`version` in the extension's `package.json`) for each release.

The builds go to `Updates.Directory`, which is `updates` next to `appsettings.json`. Users then:
- download them from **Apps & updates** in the web app, or
- install the CLI with one line:

  ```powershell
  irm https://your-server/install.ps1 | iex          # Windows
  ```
  ```bash
  curl -fsSL https://your-server/install.sh | bash   # Linux, macOS
  ```

## 10. Troubleshooting

| Symptom | Check |
|---|---|
| `HttpListenerException: Access is denied` | Bind to `127.0.0.1`, run as administrator, or add a URL ACL (section 4.4). |
| `AIProviders.OpenWebUI.ApiKey is not set` | Set `AGENTIC_AIProviders__OpenWebUI__ApiKey` and open a new console (or restart the service). |
| Agents never use cloud models | The model has no price, the balance or daily limit is used up, or the model is not in the agent's `AllowedModels`. |
| A model's circuit shows **Open** | The provider fails for that model. Run `--probe-models`, or **Reset** it on the Models page. |
| An MCP server shows disconnected | Its program, address or token is missing (section 4.5 and section 6). |
| Agents fail with "no AI models assigned" | Local models are off and the agent had no other model: give it one on its page, or price the Default ladder's first model (section 4.3). |
| Anything else | `logs\agentic-YYYYMMDD.log`, and the web app's **Live events** and **Audit** pages. |

## 11. Docker

The image `ghcr.io/smurflabs/agentic` runs the server on Linux (x64). GitHub Actions builds it from this repository's `Dockerfile` on every push to `main`. Each build checks that the server starts and answers, then publishes the image with three tags: `latest`, the version (for example `2.0.24`) and `sha-<commit>`.

### Start it with Docker Compose

You need Docker with the Compose plugin. From this repository, or from a folder holding only `docker-compose.yml` and `.env.example`:

```bash
cp .env.example .env          # set OPENWEBUI_BASE_URL and OPENWEBUI_API_KEY
docker compose up -d
docker compose logs agentic   # shows the one-time password for "admin" on the first start
```

Open `http://localhost:8080` and sign in as `admin`. Then continue with section 5 (prices, users, API keys).

### Settings

Compose passes these `.env` values to the server:

| `.env` | Setting | Default |
|---|---|---|
| `OPENWEBUI_BASE_URL` | `AIProviders.OpenWebUI.BaseUrl` | (required) |
| `OPENWEBUI_API_KEY` | `AIProviders.OpenWebUI.ApiKey` | (required) |
| `LOCAL_MODELS_ENABLED` | `LocalModels.Enabled` (section 4.3) | `false` |
| `LOCAL_MODEL_SLOTS` | `LocalModels.Slots` | `4` |
| `PUBLIC_URL` | `Server.PublicUrl` | empty |
| `AGENTIC_PORT` | the port on this machine | `8080` |

Any other setting can be added under `environment:` in `docker-compose.yml` as `AGENTIC_<Section>__<Key>`, for example `AGENTIC_ImageGeneration__ApiKey`.

### Where things are

The `agentic-data` volume holds `/data`:

| Path | What |
|---|---|
| `/data/appsettings.json` | The configuration. It is copied from the image's default on the first start; models and agents are set here (section 4). |
| `/data/data/` | Database, files, backups |
| `/data/logs/` | Logs |

To edit the configuration:

```bash
docker compose cp agentic:/data/appsettings.json .
# edit appsettings.json
docker compose cp appsettings.json agentic:/data/appsettings.json
docker compose restart agentic
```

To keep `/data` in a folder on the host instead, replace `agentic-data:/data` with `./agentic-data:/data`. The server runs as user `app` (uid 1654), so first run `sudo chown -R 1654:1654 agentic-data`.

### Updating

```bash
docker compose pull && docker compose up -d
```

To build the image yourself, run `docker build -t agentic .`, or replace `image:` with `build: .` in `docker-compose.yml`.

### Differences from Windows

- The `shell` and `windows` tool servers need Windows and are off in the container.
- The image has no Node.js or Chrome, so the `browser`, `rea` and Mermaid diagram tools are not available. Build your own image on top of this one to add them.
- `--service` is Windows-only; Compose's `restart: unless-stopped` keeps the server running instead.

## License

Agentic is free software, licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE).
