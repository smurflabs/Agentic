<div align="center">

<img src="src/Agentic/Dashboard/img/logo.png" alt="Agentic" width="288">

**A self-hosted server for teams of AI agents, and the people who work with them.**

[![Release](https://github.com/smurflabs/Agentic/actions/workflows/release.yml/badge.svg)](https://github.com/smurflabs/Agentic/releases)
[![Docker image](https://github.com/smurflabs/Agentic/actions/workflows/docker.yml/badge.svg)](https://github.com/smurflabs/Agentic/actions/workflows/docker.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPL--3.0--or--later-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Runs on Windows and Docker](https://img.shields.io/badge/runs%20on-Windows%20%7C%20Docker-lightgrey)

[Quick start](#quick-start) · [Screenshots](#screenshots) · [Features](#features) · [Services](#services-for-every-feature) · [Setup guide](SETUP.md) · [Reference manual](docs/REFERENCE.md) · [Client apps](#client-apps)

</div>

---

## What is Agentic?

Agentic runs a team of AI agents on your own infrastructure. Each agent has a role, goals, private memory, its own permissions and a budget. Agents take work from a queue, run on schedules, plan larger requests together, delegate to each other and do real work with tools such as files, git, databases, documents and the web, through the [Model Context Protocol](https://modelcontextprotocol.io) (MCP). When an agent needs a decision, it asks a person.

Around the agents, Agentic is a complete AI workspace for people. It has a chat app with tools and a code interpreter, OpenAI- and Anthropic-compatible APIs, and client apps for the terminal, the desktop and VS Code. All of them share the same accounts, permissions and models.

Agentic works with any OpenAI-compatible model provider, such as OpenWebUI, LiteLLM, vLLM, llama.cpp or a hosted API, and optionally with your own local GPU servers. Each request is routed by purpose and price, and falls back to another provider when one fails. Cloud usage is paid from a wallet with a ledger, so autonomous work stays within the limits you set.

The server is one self-contained executable with an embedded web app and a SQLite database. There is no separate web server, database server or front-end build to run.

## Screenshots

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Chat: any model, with web search, tools, pictures, music, voice and the code interpreter in the message box" width="100%"><br>
  <sub><b>Chat</b>: every model and custom model. The message box has web search, tools, pictures, video, music, markets, voice and the code interpreter.</sub>
</p>

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/overview.png" alt="Overview dashboard with agents, tasks, AI calls, local model slots and cloud spend"><br>
      <sub><b>Overview</b>: agents alive, work done, AI calls, local model slots and cloud spend against the daily cap.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/agents.png" alt="Agents list with state, queue, subagents, owner and balance"><br>
      <sub><b>Agents</b>: a team of 25, each with its own queue, subagents, models and wallet.</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/global-task.png" alt="New global task form: name, request, priority, purpose and which agents may take part"><br>
      <sub><b>Global tasks</b>: describe a goal, and the coordinator plans which agents do which part.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/global-task-review.png" alt="Global task options: deadline, plan approval, run settings and review by a manager agent"><br>
      <sub><b>Review and control</b>: plan approval, deadlines, automatic tool approval, and a reviewer agent that checks the result against the expected outcome.</sub>
    </td>
  </tr>
</table>

### Media and data tabs

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/audio.png" alt="Audio tab: text to speech with voice, language, stream format and every XTTS setting"><br>
      <sub><b>Audio</b>: speak any text with every XTTS setting, clone voices, and keep the clips (<a href="https://github.com/smurflabs/Agentic_xtts">Agentic XTTS</a>).</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/images.jpg" alt="Images tab: Stable Diffusion prompt, model and sampling settings, with a live preview of the picture being made"><br>
      <sub><b>Images</b>: Stable Diffusion 1.5 with a live preview while the picture is made (<a href="https://github.com/smurflabs/Agentic_sdimages">Agentic SD Images</a>).</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/money.png" alt="Money tab: live crypto prices and exchange rates with charts"><br>
      <sub><b>Money</b>: live crypto prices and exchange rates with charts, and a watch list for each account.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/stocks.png" alt="Stocks tab: indexes and stocks with intraday charts and a ticker lookup"><br>
      <sub><b>Stocks</b>: indexes and stocks with charts for any range, and a lookup of any US ticker.</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/news.png" alt="News tab: headlines from 42 outlets, filtered by region and outlet"><br>
      <sub><b>News</b>: headlines from 42 outlets' feeds, by region and outlet. Agents can search them too.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/polymarket.gif" alt="Polymarket tab: prediction markets with live odds, bids and asks"><br>
      <sub><b>Polymarket</b>: prediction markets live, with odds, bids, asks and volume.</sub>
    </td>
  </tr>
</table>

### Client apps

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/desktop-home.png" alt="Agentic Desktop on Windows: the web app with This computer: Skills, Local MCP and Desktop settings"><br>
      <sub><b><a href="https://github.com/smurflabs/Agentic_desktop">Agentic Desktop</a></b>: the whole web app in its own window, plus Skills, local MCP servers and tools on your computer.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/desktop-chat.png" alt="Agentic Desktop chat with the Skills and This PC chips and the tokens-per-second gauge"><br>
      <sub><b>Desktop chat</b>: skills and this computer's tools in the message box, and live speed on the model bar.</sub>
    </td>
  </tr>
  <tr>
    <td colspan="2" valign="top">
      <img src="docs/screenshots/desktop-multitask.png" alt="Agentic Desktop Multitask: two chats side by side"><br>
      <sub><b>Multitask</b>: up to four chats side by side, each answering live.</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/vscode-lite.png" alt="Agentic for VS Code in Lite mode next to VS Code's Chat and Claude Code"><br>
      <sub><b><a href="https://github.com/smurflabs/Agentic_vscode">Agentic for VS Code</a></b>, Lite mode: just the chat, next to VS Code's own Chat and Claude Code.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/vscode-lite-chat.png" alt="Agentic for VS Code answer with a runnable Python block: Run Python, Apply and Copy"><br>
      <sub><b>Code blocks in VS Code</b>: Run, Apply to a file, or Copy.</sub>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/vscode-full.png" alt="Agentic for VS Code in Full app mode"><br>
      <sub><b>Full app</b>: every page of the web app inside VS Code.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/cli-session.png" alt="Agentic CLI session: a streamed answer with folded thinking and a status footer"><br>
      <sub><b><a href="https://github.com/smurflabs/Agentic_cli">Agentic CLI</a></b>: answers stream in the terminal, with folded thinking, speed and context use.</sub>
    </td>
  </tr>
  <tr>
    <td colspan="2" valign="top">
      <img src="docs/screenshots/cli-commands.png" alt="Agentic CLI command menu after typing a slash" width="70%"><br>
      <sub><b>CLI commands</b>: type <code>/</code> for every command with its description.</sub>
    </td>
  </tr>
</table>

## Features

### Autonomous agents

- **Isolated agents (up to 50).** Each agent is a supervised loop with its own memory, files, permissions, models and wallet. No agent can read another agent's memory, tasks or credentials.
- **Tasks and schedules.** Each agent has a queue with drag-and-drop ordering, priorities, deadlines, off-peak work, and interval or cron schedules.
- **Collaboration with guard rails.** Agents ask each other for help and delegate work through one permissioned, audited message system, with loop, depth and rate limits.
- **Global tasks.** A single request is planned into steps, spread over the right agents and combined into one answer.
- **Subagents.** An agent can start temporary helpers for parallel work, each with its own spend cap.
- **Autonomy levels.** Levels range from supervised to fully autonomous. They cover budget policies, supervisor agents, initiative when idle, hiring new agents and bounties. Behaviour changes are evaluated and rolled back when they make an agent worse.
- **Human in the loop.** Questions and approvals wait in an Inbox, and any tool can require approval for each call.
- **Organizational learning.** When an agent runs out of money or earns a lot, its decisions are studied and the lessons are shared with every agent.

### Models and routing

- **Any OpenAI-compatible provider**, plus optional **local models** with a configurable number of parallel slots, or none at all.
- **Routing by purpose.** Each kind of work (coding, research, analysis, …) has an escalation ladder, and a stronger model is used only when the task needs one.
- **Resilience:**
  - fallbacks at other providers for each model
  - circuit breakers
  - out-of-credit detection
  - retries that keep the work already done
  - context windows learned from real provider responses
- **Custom models.** One model name sends each request to the model you chose for that kind of work (coding, maths, vision, long context, …), using rules, a classifier or both.
- **Bring your own model.** Users can add their own API keys and models for private use.

### Budgets and accounting

- **Wallets and an append-only ledger** in micro-USD. Every cloud call reserves its cost first and settles the real usage afterwards.
- **Price controls:** a price per model (Model Square), daily limits and global caps.
- **Agent finances:** burn-rate and runway forecasts, and profit and ROI per agent.
- **Account balances for people,** with usage per user, model and API key, and optional deposits through BTCPay.

### Tools (MCP)

- **MCP client** for stdio and HTTP servers. It reconnects and health-checks servers, controls access per agent and per user, and applies approval rules.
- **16 built-in tool servers** in the same executable:
  - files and code: shared storage, shell, git, Gitea, code review, files and OCR
  - documents and data: documents (reading and writing PDF and Office files), databases (SQLite, PostgreSQL, MySQL, SQL Server), data and charts
  - systems: network checks and SSH, Docker, Windows administration, dependency audits
  - other: email, utilities, public lookups
- **Tools loaded on demand.** Agents get a tool's description only when they need it, which keeps prompts small even with many servers.
- **Personal MCP servers** for each user, private or shared, with OAuth sign-in.

### Chat and workspace

- **Chat with every provider and custom model.** Answers stream with their reasoning. You can attach files and images, search the web (with citations), use knowledge bases (RAG), and keep projects, memories and saved prompts.
- **Tools in chat:**
  - MCP tools and a code interpreter
  - tool cards, diffs, approvals and permission modes
  - goals, plans and task lists for long work
  - artifacts with shareable links
- **Pictures, video and speech:** image and video generation, reading answers aloud, and voice dictation.
- **Live in every window.** An answer streams to every open browser tab, the desktop app and VS Code at once.
- **Optional tabs:** Audio, Images and Music (powered by [Agentic's own services](#agentics-own-services)), plus Money (crypto and currencies), Stocks, Polymarket and News.

### Operations and security

- **Operations console:** live overview, operations center, emergency stop, workflow timelines, live events, results and reviews, and a communication topology graph.
- **Accounts and permissions:** administrators and users with fine-grained permissions, agent and model scopes, per-user limits and personal API keys.
- **Secure by default:**
  - PBKDF2 password hashes, CSRF protection and a strict Content Security Policy
  - secrets taken from the environment or an encrypted vault, and redacted from logs
  - a full audit log
  - a guard against destructive shell commands
- **Built to stay up.** Tasks are checkpointed and resume after a restart, agents restart with backoff, stuck work is detected, and backups and data retention run automatically.
- **Monitoring:** `/healthz`, Prometheus metrics and alert webhooks.

### APIs and client apps

- **OpenAI-compatible API** (`/v1/chat/completions`, `/v1/models`, `/v1/embeddings`, `/v1/images/generations`) and an **Anthropic-compatible** `/v1/messages`. Existing SDKs and tools connect with an Agentic API key.
- **REST API** for everything the web app does, documented interactively with Swagger.
- **Client apps** for the terminal, the desktop and VS Code. The server distributes and updates them itself.

## Quick start

### Docker

```bash
curl -O https://raw.githubusercontent.com/smurflabs/Agentic/main/docker-compose.yml
curl -o .env https://raw.githubusercontent.com/smurflabs/Agentic/main/.env.example
# Edit .env: set OPENWEBUI_BASE_URL and OPENWEBUI_API_KEY to your model provider
docker compose up -d
docker compose logs agentic       # shows the one-time password for "admin"
```

Open <http://localhost:8080> and sign in as `admin`. The image is [`ghcr.io/smurflabs/agentic`](https://github.com/smurflabs/Agentic/pkgs/container/agentic).

### Windows

Download `Agentic-<version>-win-x64.zip` from [Releases](https://github.com/smurflabs/Agentic/releases), extract it to `C:\Agentic`, and start at the `setx` line below. To build from source instead, you need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/smurflabs/Agentic.git
cd Agentic
dotnet publish src\Agentic\Agentic.vbproj -c Release -r win-x64 -o C:\Agentic
setx AGENTIC_AIProviders__OpenWebUI__ApiKey "sk-..."      # then open a new console
# In C:\Agentic\appsettings.json, set AIProviders.OpenWebUI.BaseUrl and your models
C:\Agentic\Agentic.exe --validate-config
C:\Agentic\Agentic.exe
```

Open <http://127.0.0.1:8080> and sign in as `admin` with the password printed on the first start.

**Next steps.** Point the model keys at your provider's models, set their prices, create accounts, and decide whether you run local models. [SETUP.md](SETUP.md) walks through each step.

## Client apps

| App | Repository | What it does |
|---|---|---|
| **Agentic CLI** | [Agentic_cli](https://github.com/smurflabs/Agentic_cli) | A coding agent in your terminal: the tools run on your machine, the models on your server. |
| **Agentic Desktop** | [Agentic_desktop](https://github.com/smurflabs/Agentic_desktop) | The web app in a window of its own on Windows and Linux. It adds local MCP servers, Claude Code skills, and file and command tools that ask for approval. |
| **Agentic for VS Code** | [Agentic_vscode](https://github.com/smurflabs/Agentic_vscode) | Chat inside VS Code, with tools that read, edit and run code in your workspace. |

Each repository's **Releases** page has ready-to-run builds. Every client signs in with a personal API key: in the web app, open the user menu and choose **API key**. An administrator can also publish the clients from the server (`tools/publish-clients.ps1`). People then download them from **Apps & updates**, and installed copies update themselves.

## Services for every feature

Agentic needs only an OpenAI-compatible model provider. To use every feature, also install the services below. Each one is optional: without it, the features it powers are off or hidden, and everything else keeps working.

### Third-party services

| Service | Repository | What it enables in Agentic | Setting |
|---|---|---|---|
| **Open WebUI**: one OpenAI-compatible API in front of local and cloud models | [open-webui/open-webui](https://github.com/open-webui/open-webui) | The models for agents, Chat and the API, and speech to text for dictation | `AIProviders.OpenWebUI` |
| **Open Terminal**: a sandboxed Linux computer with a shell, files and processes, run as a Docker container | [open-webui/open-terminal](https://github.com/open-webui/open-terminal) | Remote code execution: the `code-exec` tools for agents, the chat's code interpreter and Run buttons, the sandbox Files browser and app previews | `McpServers` â†’ `code-exec` |
| **open-terminal-mcp-auth**: an authenticating proxy in front of Open Terminal's MCP endpoint | (your own deployment) | Bearer-token access to the sandbox; Agentic sends `MCP_CODE_EXEC_TOKEN` | `MCP_CODE_EXEC_TOKEN` |
| **SearXNG**: a private metasearch engine | [searxng/searxng](https://github.com/searxng/searxng) | Web search for agents and Chat, and deep research, without a search API key | `WebSearch.UrlTemplate` |
| **Crawl4AI**: an LLM-friendly web crawler and scraper | [unclecode/crawl4ai](https://github.com/unclecode/crawl4ai) | Reading and crawling whole websites as clean Markdown; it runs as a container on the sandbox host, next to Open Terminal | (on the sandbox host) |

### Agentic's own services

Speech, pictures and songs come from three small self-hosted servers, each in its own repository. A tab whose server is not set up stays hidden.

| Service | Repository | What it enables in Agentic | Setting |
|---|---|---|---|
| **Agentic XTTS**: Coqui XTTS-v2 text to speech, with voice cloning and streaming | [Agentic_xtts](https://github.com/smurflabs/Agentic_xtts) | Read aloud and Dictation mode in Chat, the Audio tab, and voices in the CLI and the apps | `TextToSpeech.BaseUrl` |
| **Agentic SD Images**: Stable Diffusion 1.5 with LoRAs, ControlNet, inpainting and hires fix | [Agentic_sdimages](https://github.com/smurflabs/Agentic_sdimages) | The Images tab and the chat's picture tool | `StableDiffusion.BaseUrl` |
| **Agentic Suno**: songs and lyrics with your Suno account | [Agentic_suno](https://github.com/smurflabs/Agentic_suno) | The Music tab | `Music.BaseUrl` |

Each of Agentic's own services has its own API key. An administrator pastes it on the tab's **Settings** page, where it is kept in Agentic's encrypted vault and never reaches a browser. [SETUP.md](SETUP.md#6-optional-tools-and-services) covers installing and connecting every service.

## How it works

```mermaid
flowchart LR
    subgraph clients [Clients]
        web[Web app]
        cli[CLI]
        desk[Desktop app]
        code[VS Code]
        sdk[OpenAI / Anthropic SDKs]
    end
    subgraph server [Agentic server: one executable]
        http[HTTP API and web app]
        agents[Agents, tasks, schedules]
        bus[Message bus]
        gateway[Model gateway: routing, fallbacks, budgets]
        tools[MCP tool servers]
        db[(SQLite)]
    end
    providers[Model providers]
    local[Local model servers, optional]
    ext[External MCP servers]

    web & cli & desk & code & sdk --> http
    http --> agents
    agents <--> bus
    agents --> gateway
    http --> gateway
    agents --> tools
    gateway --> providers
    gateway --> local
    tools --> ext
    agents --> db
    http --> db
```

- **One way between agents.** Agents never talk to each other directly. Every request, delegation and reply goes through the message bus, which checks permissions and limits and records everything.
- **One way to the models.** Every model call goes through the model gateway. The gateway chooses the model the agent is allowed to use and can afford, waits for a local slot or spills over to the cloud, and charges the agent's wallet.
- **Tools in separate processes.** Built-in tool servers run as child processes of the same executable, and external MCP servers connect over stdio or HTTP. Each agent sees only the tools it was granted.

## Configuration

All settings live in `appsettings.json`, which allows comments. Any setting can be overridden with an environment variable named `AGENTIC_<Section>__<Key>`, for example `AGENTIC_Server__Port=9090`. Keep secrets in environment variables or in the server's encrypted vault, never in the file.

| Section | What it sets |
|---|---|
| `AIProviders` | The model provider: base URL and API key |
| `Models` | The models agents and chat can use, by key, with capabilities, prices and fallbacks |
| `LocalModels` | Whether you run local models, and how many requests each serves at once |
| `Routing` | Escalation ladders, spillover from local to cloud, and fallback behaviour |
| `BaseProfiles`, `Agents` | The agents: roles, goals, models, tools, budgets and autonomy |
| `McpServers` | Tool servers and who may use them |
| `Server`, `Admin` | Address, port, public URL, users and API tokens |

`Agentic.exe --validate-config` checks the whole file and names each problem. The [reference manual](docs/REFERENCE.md#7-configuration) describes every section.

## Documentation

- **[SETUP.md](SETUP.md):** installation and configuration, step by step, on Windows or with Docker.
- **[docs/REFERENCE.md](docs/REFERENCE.md):** the complete manual covering agents, routing, the economy, MCP servers, chat, the APIs and operations.
- **Swagger:** in the web app, an interactive reference for the REST API.

## Building and testing

```powershell
dotnet build Agentic.slnx
dotnet test  Agentic.slnx
```

The test suite has about 800 xUnit tests. They run against a scriptable fake model provider and temporary SQLite databases.

| Folder | Contents |
|---|---|
| `src/Agentic` | The server: agents, routing, economy, MCP, HTTP API, and the embedded web app (`Dashboard/`, plain JavaScript, no build step) |
| `tests/Agentic.Tests` | The test suite |
| `samples/EchoMcpServer` | A minimal MCP server, used as a template and in the tests |
| `tools/` | Publish scripts, the MSI builder and the Node.js tool packages |
| `docker/`, `Dockerfile`, `docker-compose.yml` | The container image |

How it is built:
- VB.NET on .NET 10, with `Option Strict On` throughout
- `HttpListener` instead of ASP.NET
- SQLite in WAL mode
- the official MCP C# SDK
- every package version pinned

## Security

The server listens on `127.0.0.1` by default. To reach it from other machines, put an HTTPS reverse proxy in front of it, or use a VPN or private network. Do not expose it directly to the internet. The tools agents use can act on the machine they run on, so grant them with care. Shell commands need an administrator's approval by default.

Please report security issues privately through this repository's **Security** tab, not in public issues.

## License

Agentic is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

Bundled third-party files keep their own licenses: KaTeX (MIT) and Swagger UI (Apache-2.0), in `src/Agentic/Dashboard/vendor/`.
