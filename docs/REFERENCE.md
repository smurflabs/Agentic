# Agentic reference

The complete manual: every feature, setting and API in depth. For an overview see the [README](../README.md); to install, see [SETUP.md](../SETUP.md).

A single-executable **VB.NET (.NET 10)** host for up to **50 isolated but collaborative AI agents**. It includes:

- an embedded `HttpListener` admin dashboard
- SQLite persistence
- an OpenWebUI (OpenAI-compatible) AI provider
- MCP client support
- a per-agent **economy**: agents spend a budget on cloud models, and the admin credits the profit they generate
- **subagents**: an agent can spawn temporary helpers for parallel subtasks; local-model work is free, cloud use is charged to the parent
- **additional model access**: administrators register extra cloud models and grant them to specific agents, stating what each is for
- an operations console with full control: live operations center, emergency stop, a drag-and-drop task queue per agent, and a form-based agent editor
- **organizational learning**: when an agent goes broke or becomes very profitable, the host studies the decisions that got it there and shares the lessons with every agent

No IIS, ASP.NET, Kestrel, Node.js, Docker or separate web server is required.

> **Isolated but collaborative.** Each agent has private state and its own permissions. Agents can ask each other for help, delegate work and return results, but only through the host-controlled, permissioned, persistent, audited message system. No agent can read another agent's memory, tasks, credentials or runtime.

**Setting it up:** [SETUP.md](../SETUP.md) walks through building, configuring and running the server, step by step, on Windows or in Docker. The client apps live in their own repositories: **Agentic_cli** (terminal), **Agentic_desktop** (desktop app) and **Agentic_vscode** (VS Code extension).

---

## Contents
1. [Quick start](#1-quick-start)
2. [Architecture](#2-architecture)
3. [Models and routing](#3-models-and-routing)
4. [Agent economy and organizational learning](#4-agent-economy)
5. [Agents: profiles, subagents, model access, task queue, isolation, communication, autonomy](#5-agents)
6. [MCP servers](#6-mcp-servers)
7. [Configuration reference](#7-configuration)
8. [Security](#8-security)
9. [Dashboard and API: Chat, custom models, API keys, OpenAI-compatible API, client app updates, Swagger](#9-dashboard-and-api)
10. [Operations: deployment, service, backup, maintenance, troubleshooting](#10-operations)
11. [Development and tests](#11-development-and-tests)

---

## 1. Quick start

```powershell
# 1. Build a self-contained single-file exe
dotnet publish src\Agentic\Agentic.vbproj -c Release -r win-x64 -o C:\Agentic

# 2. Provide the API keys as environment variables (never in the config file)
setx /M AGENTIC_AIProviders__OpenWebUI__ApiKey "sk-..."     # machine-wide; open a new console afterwards
setx /M AGENTIC_ImageGeneration__ApiKey "sk-..."            # the images API (ImageGeneration)
setx /M AGENTIC_VideoGeneration__ApiKey "sk-..."            # the video API (VideoGeneration)

# 3. Check configuration and models
C:\Agentic\Agentic.exe --validate-config
C:\Agentic\Agentic.exe --probe-models          # chat, tool calls and vision for every model
C:\Agentic\Agentic.exe --probe-image           # same, plus one image generation (costs money)

# 4. Run
C:\Agentic\Agentic.exe
```

On first start, if `Admin.Users` is empty, the console prints a one-time password for the `admin` user. Open the dashboard at `http://127.0.0.1:8080` and sign in. To make a permanent user, run `Agentic.exe --hash-password` and add the hash to `Admin.Users` (see [Security](#8-security)).

**Before agents may use cloud models, set prices.** Every cloud model ships unpriced, and an unpriced model is never used (fail-closed). Set USD per 1M input and output tokens in `appsettings.json` (`Models[].Pricing`) or in the dashboard under **Finance → Model pricing**. Also set a per-image price (`image-generation`) if agents should generate images. Until prices are set, agents run only on the free local model. A server without local models sets `LocalModels.Enabled` to `false` ([SETUP.md](../SETUP.md#43-local-models)); its agents then need priced cloud models.

Command line:

| Option | Purpose |
|---|---|
| `--config <path>` | Config file (default: `appsettings.json` in the working directory, then the exe folder) |
| `--port <n>` / `--bind <host>` | Override `Server.Port` / `Server.Host` |
| `--validate-config` | Validate everything and exit (exit code 1 if invalid) |
| `--list-agents` | Print the resolved agents, models, recipients, MCP access and schedules |
| `--probe-models` / `--probe-image` | Live check of every configured model against the provider |
| `--hash-password [pw]` | Print a PBKDF2 hash for `Admin.Users[].PasswordHash` |
| `--service` | Run as a Windows Service (used by `sc.exe`) |
| `--version`, `--help` | |

---

## 2. Architecture

```
┌───────────────────────────────── Agentic.exe ────────────────────────────────┐
│  HttpListener admin UI + JSON API + SSE live feed (auth, CSRF, audit)           │
│                                   │                                             │
│  AgentManager ── owns runtimes privately, exposes snapshots + commands only     │
│     │  Agent 001 … Agent 050: each one supervised async loop                    │
│     │  (heartbeat → pause gate → mailbox → expired waits → next task → execute) │
│     ▼                                                                           │
│  AgentCommunicationService (the only path between agents)                       │
│     permission → type → size → priority cap → rate limit → mailbox cap →        │
│     outstanding requests → loop/depth/chain checks → persist → event → wake     │
│                                                                                 │
│  Task engine: queue, scheduler (interval/cron), executor, handlers              │
│     reasoning (bounded AI/tool loop, checkpointed) · http-check · mcp-tool · noop│
│                                                                                 │
│  AI: ModelGateway → selection (permissions, tier, purpose, price) →             │
│      local priority gate (N slots, aging) → spillover if affordable →           │
│      circuit breakers + fallbacks → OpenAI-compatible streaming client          │
│  Economy: wallets, append-only ledger, reserve→settle, pricing                  │
│  MCP: server manager (stdio/HTTP, reconnect), permission-filtered tools         │
│  Event bus → activity log, statistics, SSE, alerts webhook                      │
│  SQLite (WAL): agents, tasks, memory (FTS5), messages, ledger, audit, …         │
└─────────────────────────────────────────────────────────────────────────────────┘
```

Source layout (`src/Agentic`):

| Folder | Contents |
|---|---|
| `Agents/` | `AgentRuntime` (supervised loop), `AgentManager` (lifecycle, snapshots, watchdog), `ProfileRegistry` |
| `Communication/` | `AgentMessage`, `AgentCommunicationService` + agent-bound `AgentCommunicationClient`, `MailboxProcessor`, `LoopDetector`, rate limiter |
| `Work/` | `AgentTask`, `TaskExecutor` + deterministic handlers, `WorkScheduler`, `TaskCheckpoint`/`WaitResolver` |
| `AI/` | `OpenAICompatibleProvider`, `ModelRegistry` (circuit breaker, priority gate), `ModelSelectionService`, `ModelGateway`, `PromptBuilder`, `ReasoningEngine`, vision/image services, `ModelProbe` |
| `Economy/` | `WalletService` (ledger), `PricingService`, agent-bound `AgentBudgetView` |
| `Mcp/` | `IMcpClient` + `SdkMcpClient` (official SDK), `McpServerManager`, `McpToolRegistry` |
| `Tools/` | `IAgentTool`, `AgentScope` (the isolation boundary), internal tools, schema validation |
| `Memory/`, `Artifacts/`, `Approvals/` | Private memory, owned artifacts with explicit grants, human-in-the-loop |
| `Persistence/` | SQLite access, embedded migrations, repositories, retention, backups |
| `Http/` | Server, router, auth, admin API, SSE |
| `Dashboard/` | Embedded vanilla-JS SPA (no build step) |

**Dependencies.** All versions are pinned.

| Package | Version | Why |
|---|---|---|
| Microsoft.Data.Sqlite | 10.0.12 | SQLite with a bundled native library, including FTS5 for memory search |
| ModelContextProtocol.Core | 2.2.0 | Official MCP C# SDK, client and server core with no hosting dependency |
| Microsoft.Extensions.Configuration.Json / .EnvironmentVariables / .Binder | 10.0.12 | `appsettings.json` with comments, env-var overrides, typed binding |
| Cronos | 0.13.0 | Cron expressions for schedules |
| System.ServiceProcess.ServiceController | 10.0.12 | Windows Service mode |

JSON, HTTP, the HTTP server, channels and logging are in-box.

---

## 3. Models and routing

Models are the entries in `Models[]`. Agents, ladders and fallbacks refer to them by key, so the keys stay the same while each `ModelId` points at a model your provider serves. The shipped configuration defines these keys:

| Key | Role | Fallbacks (in order) |
|---|---|---|
| `local-main` | **Default thinking model for every agent.** A local model server: free, `LocalModels.Slots` slots, no vision. Agent work is shared with `local-vision` all the time (`Routing.Spillover.LocalBalance`, see below), and `local-vision` also takes text work while this one is full (`Routing.Spillover.LocalCandidates`, keeping `LocalKeepFreeSlots` = 1 free), before any cloud spillover. | `local-vision` |
| `local-vision` | A second local model that sees images: free, `LocalModels.Slots` slots shared by chat and agents. Images, and text work `local-main` cannot take. | |
| `coding-t2` | Coding beyond the local model. | `coding-t2-alt`, `coding-t2-alt2` |
| `language-t2` | Complex language and analysis work. | `language-t2-alt`, `language-t2-alt2` |
| `expert-t3` | Advanced coding, reverse engineering and the hardest work. Sees images. | `expert-t3-alt`, `expert-t3-alt2` |
| `vision` | Image understanding, and the cheapest spillover target. | `vision-alt`, `vision-alt2` |

- **Fallbacks.** Give each tier fallbacks at other providers, so an agent still has a model when one provider has trouble. A fallback stands in for its primary, so it needs no grant of its own.
- **Local models are optional.** With `LocalModels.Enabled: false` the two local keys and every reference to them are dropped, and agents work on the cloud tiers ([SETUP.md](../SETUP.md#43-local-models)).
- **Prices.** Agents are billed per key at the Model Square price of the model the key calls. At startup each key's price follows the Model Square price of its `ModelId`, so pointing a key at another model never leaves it at the old price.
- **Pictures and videos.** Pictures come only from the images API in `ImageGeneration` (its `Model`, then `FallbackModels`), and videos only from the video API in `VideoGeneration`, never from the chat provider.

**Escalation ladders.** Each purpose has a ladder in `Routing.Ladders`:

- Default, ComplexLanguage, ComplexAnalysis and Research: `local-main → language-t2 → expert-t3`
- CodeGeneration, AdvancedCoding and ReverseEngineering: `local-main → coding-t2 → expert-t3`

At each level, the model's `Fallbacks` are tried before the host moves up a level.

The host, not the model, decides when to escalate (`Routing.Escalation`). It escalates when:

- the model is unavailable after fallbacks
- the model returns empty or truncated output twice
- a task stops making progress (a whole stretch of `MaxReasoningTurns` turns with no new results, more refused repeats than new results, or only reading for four checks in a row)
- a retry is needed and `EscalateOnRetry` is set
- the agent calls `request_escalation`

Escalation is capped by the agent's `AllowedModels`, its `MaxTier`, `MaxEscalationsPerTask` and its budget. If the agent can't afford the stronger model, the task continues on the current one.

**No fixed turn limit.** `Tasks.MaxReasoningTurns` (16) and `Tasks.MaxToolCalls` (40) set how often a task's progress is checked; they are not totals. When a stretch ends, a task that got new results in it goes on (its context is kept compact every turn). One that only failed or repeated itself gets a stronger model once, then stops with a report of its work. New results are not always progress: a stretch in which the loop guard refused as many repeated calls as there were new results counts as going in circles, and four checks in a row with only reading (reads, lists, searches, re-planning; no write, command, saved note or newly finished plan step) count as stuck. A shell command that only reads (`cat`, `sed -n`, `grep`, `ls`, `find`, `git log`/`diff`/`status`, with no output redirect) counts as reading too. A local model once read the same repository for two hours, 600 reads in new line ranges and not one write, and passed every check. A plan that marks finished steps as not done again gets a note not to redo the work, and redoing them is not counted as progress.

**Agent context.** When a task's transcript passes 70% of its smallest model's window, or its message limit (`Tasks.MaxContextMessages`, raised to one message per 2000 tokens of window, at most 160), older tool outputs are shortened and the oldest turns removed, down to 60%. The start of the transcript then stays the same for many turns, so a model server can answer from its prompt cache. A host note keeps what the removed turns did (the file and lines, the command; the newest 40). An output identical to one still in the transcript is not repeated: the agent gets a short note that points at it, and that note gets the whole output before the first copy is shortened or removed.

**Delegated work ends with its delegation.** When a delegation expires (the delegator stopped waiting and was told so), the delegated task is cancelled wherever it is, also while it runs.

**Closed global tasks stay closed.** For 30 days, an agent's scheduled, recurring, delegated and message-triggered tasks list the global tasks it worked on (a step or the review) that finished without the reviewer's approval. The agent is told they are closed and that what happens next is the administrator's decision, so it must not start follow-up work on them by itself. Without this, a manager's daily review can keep delegating "follow-up" on a finished project for hours each day. Tasks you give an agent yourself don't get this list. `Tasks.MaxTotalTurns` (0 = none) adds a hard total if you want one. `Tasks.MaxTaskDurationSeconds` (1800) is a time slice: from half of it, a task still making progress pauses at the end of its current turn when another task of its agent is waiting, and goes on from its checkpoint without using a retry; with nothing else waiting, its slice starts over instead. A turn therefore always has half a slice to finish and is never cut off by the slice. Waiting for the AI (a model server down, rate limits, the agent's AI quota or budget used up) does not use retries either: the task tries again every few minutes for up to 24 hours. A model server that is down behind LiteLLM (HTTP 400 "InternalServerError ... Connection error") counts as an outage, not a bad request.

**Sharing the local models.** With `Spillover.LocalBalance` (on), each agent task goes to the local model (`FromModel` or one of `LocalCandidates`) with the fewest tasks working on it per slot, and stays there while that model has a free slot, so the server keeps the task's prompt in its cache. A task counts as working on a model for `LocalBalanceWindowSeconds` (600) after its last call. Four agents on one llama.cpp server overfill its shared context, and every call is then processed from scratch (210-240 s per call in one test, while `local-vision` sat idle); two per server took 30-40 s.

**Local queue and spillover.** All agents share the 4 local slots through a priority queue with aging, so Low-priority work is never starved. When a request has waited longer than `Spillover.<Priority>AfterSeconds`, the host tries the cheapest healthy cloud model in `Spillover.Candidates` that the agent is allowed to use and can afford. If none qualifies, the request keeps its place in the queue and waits, up to `QueueTimeoutSeconds`.

**Providers out of credit.** When a provider answers that its account has no credit left (`insufficient_quota`, "insufficient balance", the relays' 额度/余额 messages), every model of that provider (the model id before `/`) is paused for `Routing.OutOfCreditPauseMinutes` (30): their circuits open, fallbacks on other providers answer meanwhile, and nobody keeps calling it. A task whose level has no working model left (or that has waited 10 minutes for the AI) moves to another level of its ladder that works — the nearest cheaper one first, down to the free local model — instead of waiting for hours.

Administrators are told once per provider: a notice on every dashboard page (and a toast with a sound), a message on the configured notification channel (`Notifications`: webhook or email), and an **Out of credit** card on **Models & access**. After adding balance at the provider, **Turn back on** (in the notice or the card, `POST /api/models/providers/{provider}/resume`) closes its models' circuits at once. Without it, the provider is tried again by itself when the pause ends, and the notice goes as soon as one of its models answers again.

**Real context windows.** Configured window sizes are often guesses, so the host learns each cloud model's real window from what its provider does with agents' and chat users' requests: a refusal as too long sets it (providers usually name the number, e.g. "maximum context length is 65536 tokens"; without one, a little under the refused size), and a prompt the provider counted and answered proves it is at least that big. Learned windows are kept (`ModelContextLimits`), used for agents' context compaction, chat rounds and the context meter, and listed under **Context windows learned** on **Models & access**, where **Forget** returns to the configured size.

**Agent requests.** A question (`request_agent_help`) or delegated task waits at least `Communication.MinAgentToAgentWaitSeconds` (1 hour) for its answer, whatever the asking agent sets, and at most `MaxAgentToAgentWaitSeconds` (4 hours).

**Capabilities and purposes.** Each model declares `Capabilities` and `AllowedPurposes`, and selection filters on both. Runtime errors such as "model doesn't support images" are classified so the host moves to another model instead of retrying.

**Streaming.** OpenWebUI sits behind Cloudflare, which cuts non-streaming responses at about 100 s. Streaming (`UseStreaming: true`) keeps long reasoning calls alive, and the client aggregates streamed tool calls. A Cloudflare 524 is treated as a transient error.

---

## 4. Agent economy

- **Wallets.** Every agent has a wallet: balance, reserved, spend today, lifetime spend, profit credited and an optional daily limit.
- **Ledger.** All money is stored as integer micro-USD in an append-only ledger. Entry types are InitialGrant, AiCharge, ImageCharge, ProfitCredit, AdminAdjustment and Refund.
- **Reserve → call → settle.** Before a cloud call, the host atomically reserves an estimate: prompt tokens plus `max_tokens`, at the model's price. After the call it charges the actual usage and releases the rest. A call that can't be reserved never happens; this applies to spillover and escalation too. A global cap also applies (`Economy.GlobalDailyCloudLimit`).
- **Token limits are for the cloud.** An agent's `Ai.MaxTokensPerDay` (100M by default) and `MaxTokensPerHour` count cloud tokens only: the local models are free and never limited by them, and once the cloud tokens are used up the agent goes on with its local model (an escalated task drops back to its starting level). The call limits (`MaxAiCallsPerDay`, `MaxAiCallsPerHour`) count every call. The wallet is what bounds cloud spending.
- **Unreliable usage reports.** Some upstreams report implausible usage; one reported 2 prompt tokens for an image prompt. When that happens the host bills its own estimate and flags the call `UsageEstimated`.
- **Agents know their budget.** Every prompt includes the agent's balance, spend and model prices, and the `get_my_budget` tool returns the same information.
- **Runway forecast.** The host computes each agent's burn rate (average daily cloud spend over `Economy.RunwayWindowDays`, default 7) and how long its balance lasts at that rate.
  - Agents see the forecast in their budget line.
  - Below `Economy.LowRunwayDays` (default 3), an agent is told to prefer the free local model and avoid paid escalation, and it is flagged on the Overview, the Agents list, Finance and its Budget tab.
  - The goal is to correct course *before* the agent goes broke.
- **Profit.** Under **Finance** (or `POST /api/agents/{id}/profit`), record the profit an agent generated. It is a one-time credit, added to the balance and counted in ROI (profit ÷ spend). Each form carries an idempotency key, so a double submit credits only once. You can optionally name the completed task that produced the profit (`taskId`); learning studies then know which work paid off.
- **Delegated work.** The agent doing the work pays for its own AI usage. Workflow cost is also summed per correlation id and shown on the delegation tree.
- **Subagents** have no wallet. Their cloud usage is charged to the parent's wallet (ledger entries record the subagent in SpentByAgentId) and is limited by a per-subagent cap, the parent's daily limit and the parent's AI quotas. Local-model use costs nothing.
- **Granted models** are charged like any other cloud model, to the agent (or parent) that uses them.

### Account balances and Model Square

People pay for what they use from their account's balance.

- **Balances.** Every dashboard account has a balance. It starts at $0.00, and administrators add money (or take it away) under **Users & access** (**Add money**) or **Balance**. Each movement is recorded in the account's history (`AccountLedger`).
- **Model Square** (under Platform) sets the price of every model in USD per 1M input and per 1M output tokens. It covers every provider model and every custom model. A model without a price is free.
  - A custom model's own price comes before the price of the model it routes to.
  - Setting a price there also prices the agents' cloud model keys that use that model. The Finance pricing editor writes back to Model Square.
- **What is charged.** User accounts pay the Model Square price for:
  - their chats, including the desktop app and the CLI;
  - their API calls (`/v1`).
  - Each answer shows what it cost, and the balance is in the account menu.
  - With an empty balance, models with a price don't answer: Chat shows the reason, and the API answers 402.
- **Agents belong to an account.** An agent created by a user belongs to that user; an administrator can give any agent to an account in the agent editor (**Owner**).
  - An agent's cloud use is charged to its wallet, which acts as its spending limit, and to its owner's balance.
  - When a user's balance is empty, all of that user's agents stop: the running task is cancelled, subagents end, and the agent waits, paused. When money is added, the agents continue by themselves.
  - Agents of administrators (and agents with no owner) never stop for money.
- **Administrators are never charged.**
- **Pages.** **Balance** shows your balance, what you spent (today, 30 days, by model) and your history; administrators also see every account. On **Finance**, **Total balance** is your account's balance; the agents' wallets are listed below it.

| Endpoint | |
|---|---|
| `GET /api/account` | Your balance, spend, history and agents (administrators: every account) |
| `POST /api/users/{username}/balance` | `{amount, note}`: add money (negative takes it away); administrators |
| `GET /api/model-square` | Every model you may use, with its price |
| `PUT /api/model-square` | `{prices: [{model, inputPerMillion, outputPerMillion}]}`; administrators |

### Organizational learning

When an agent goes broke or becomes very profitable, the host studies how it got there, and the whole team learns from it.

**Triggers** (each with a cooldown per agent, so one bad day produces one study):

| Outcome | Fires when | Trigger |
|---|---|---|
| Failure | A cloud call is refused for lack of funds, or the available balance falls below `BrokeThreshold` | `Broke` (no money left) or `LowBalance` |
| Success | A profit credit of at least `LargeProfit` (bypasses the cooldown) | `Profit` |
| Success | Lifetime profit ≥ `MinProfit` and ROI ≥ `MinRoi` | `RoiMilestone` |
| Any | An administrator clicks **Study now** (Learning page or the agent's Learning tab) | `Manual` |

Only agents that have actually spent money are studied for failure. An agent that was simply never funded is not.

**Case study.** The host collects evidence for the window since the agent's previous study (at most `WindowDays`):

- spend by model and by reason (primary, fallback, spillover, escalation), including the agent's subagents
- task outcomes and the most common errors
- profit credits and the tasks they were attributed to
- the costliest and most profitable tasks, each with its tool sequence and model calls

The evidence goes to the **free local model** (`Learning.ModelKeys`; cloud models are rejected by validation) with instructions to explain the decisions that led to the outcome and to write short, reusable lessons. Each lesson is a **Do** or an **Avoid**. If the local model is unavailable or answers with something unusable, a rule-based analysis is used instead: escalation and spillover share, subagent cost, failure rates, the work that earned the profit.

**How agents learn.**

- **Prompts.** Every agent's prompt includes up to `PromptLessons` relevant lessons (pinned lessons first, then lessons matching the current task, then recent ones), marked as coming from a teammate or from the agent itself.
- **Search.** Agents can look lessons up with the `search_lessons` tool.
- **Memory.** The studied agent also keeps its own lessons in its private memory.

Lessons are wrapped as untrusted data, like any other agent-produced text, so a lesson can inform a decision but cannot instruct an agent.

**Confirmation instead of duplication.**

- The analyst sees the existing lesson library. When a new study reaches a conclusion the library already holds, it **confirms** that lesson by id instead of writing it again.
- New lessons whose title matches an existing lesson are also turned into confirmations.
- Each lesson counts the case studies that support it ("confirmed by 3 case studies"). Well-confirmed lessons rank higher in prompts and stay eligible after the 30-day recency window.
- Re-analyzing a study replaces its confirmations rather than counting them twice.

**Admin control** (dashboard **Learning** page and each agent's **Learning** tab):

- read every study: its headline, key decisions, root causes, recommendations and raw evidence
- approve, retract, pin or edit lessons
- re-run an analysis
- start a study manually

With `RequireApproval: true`, new lessons stay in **Pending review** and are not shown to agents until an administrator approves them.

---

## 5. Agents

### Profiles
`BaseProfiles` hold defaults: system prompt, safety rules, task, AI, communication and memory policies, and the starting balance. Each entry in `Agents` overrides individual fields. Lists **replace** the inherited list rather than merging with it.

| Area | Fields |
|---|---|
| Identity | `Id`, `Name`, `Role`, `Description`, `Personality`, `SystemPrompt` / `AppendSystemPrompt`, `Goals` |
| Models | `AllowedModels`, `MaxTier`, `DefaultPurpose`, `Ai.Max*` (calls and tokens per hour/day) |
| Tools | `InternalTools`, `AllowedMcpServers`, `AllowedMcpTools` (`server:tool` or `server:*`), `DeniedMcpTools` (deny wins) |
| Communication | `Communication.AllowedRecipients`, `AllowedSendMessageTypes`, `AllowedReceiveMessageTypes`, `MaxSendPriority`, `Groups`, `CanBroadcast`, rate limits |
| Work | `Tasks.*` (turns, tool calls, duration, retries, `EscalateOnRetry`), `Schedules[]` (interval or cron; handler `reasoning`, `http-check`, `mcp-tool` or `noop`) |
| Memory | `Memory.*` (max memories, recall count, auto-record results) |
| Money | `Economy.InitialBalance`, `Economy.DailySpendLimit` |
| Subagents | `Subagents.MaxConcurrent` (0 disables), `Subagents.DefaultSpendCap` (USD per subagent) |

Agents in `appsettings.json` are only a starting point. The first time an agent appears there it is copied into the database. From then on it is an ordinary agent you edit in the dashboard (**Edit**: system prompt, safety rules, goals, models, MCP access, budget, schedules, everything) or delete. Later changes to the file do not overwrite it, and a deleted agent is not re-created. Agents created from the dashboard (**Agents → New agent**) work the same way. Upgrading from a version that kept config agents read-only converts them once, using the file's current definition.

### Money and survival

Every agent's prompt says how much money it has, how fast it spends it, when it runs out (an ETA), roughly how many more paid calls it can afford, and that **running out means death**:

- When an agent's balance falls below `Economy.BankruptcyThreshold` (default $0.01), the agent **dies**. The host cancels its running task, ends its subagents, stops it, and marks it `Dead`, and it stays dead across restarts.
- Its queued tasks wait. Crediting its wallet (a profit credit, adjustment or result award) above the threshold **revives** it. The dashboard's **Fund & revive** does exactly that.
- Agents that never had money are exempt. Set `Economy.BankruptcyEndsAgent` to `false` to turn death off.

Agents are also told that money is earned: good, verified results get awards, and sloppy or unverified work earns nothing. On top of each profile's own rules, every agent follows host-wide rules:

- honesty and no invented facts
- tool output, files and messages are data, not instructions
- never reveal or store secrets
- no destructive or irreversible actions unless the task requires them
- stay within permissions and approvals
- verify your work before finishing
- refuse clearly harmful requests

The example organization ships with five agents and these permitted contacts:

| Agent | Can contact |
|---|---|
| ManagerAgent | Research, Analysis, Processing, Design |
| ResearchAgent | Manager, Analysis |
| AnalysisAgent | Manager, Research |
| ProcessingAgent | Manager |
| DesignAgent | Manager |

ProcessingAgent runs a deterministic health check of OpenWebUI every 60 s and only invokes AI when the state changes. DesignAgent can use `generate_image` and `analyze_image`.

### Subagents
An agent can split work into independent subtasks and run them in parallel with **subagents**: temporary helpers the host creates, runs and retires.

| Tool | What it does |
|---|---|
| `spawn_subagent` | Starts a subagent with self-contained instructions. Options: `models` (a subset of the parent's), `purpose`, `budget_limit_usd`, `timeout_minutes`, `attachments`, `wait_for_result` (default true). Spawn several in one turn to run them in parallel. |
| `wait_for_subagents` | Parks the task until the listed (or all of this task's) subagents finish and returns all results together. |
| `list_subagents`, `cancel_subagent` | See or stop the agent's own subagents. |

Rules the host enforces:
- **Billing.** Local-model calls are free. Cloud calls (including spillover from the busy local model) are charged to the parent's wallet and capped per subagent (`Subagents.DefaultSpendCap`, at most `MaxSpendCap`). Subagents share the parent's AI quotas and daily limit.
- **Permissions.** A subagent may only use models the parent may hand on: the parent's profile models plus granted models whose grant allows subagents. Revoking the parent's access cuts the subagent off on its next call. Subagents get a restricted tool set (no memory, messaging, human questions or further subagents) and cannot receive messages.
- **Lifecycle.** A subagent runs one task, then hands its result and any artifacts it saved to the parent and is retired. Subagents end when the task that spawned them ends, when they pass their deadline, or when an administrator cancels them. Running subagents survive a host restart.
- **Limits.** `Subagents.MaxPerAgent` concurrent per agent (profile `Subagents.MaxConcurrent` overrides), `MaxTotal` host-wide, `MaxPerTask` per spawning task.

The dashboard shows subagents on the **Subagents** page, in the **Operations center**, on the parent's **Subagents** tab and nested in the workflow tree.

### Additional model access (custom models and grants)
Administrators can give individual agents access to cloud models beyond their profile:
- **Register a custom model** (**Models & access → Register model**): any model id the provider offers, with prices (required), capabilities and limits. Custom models are stored in the database, never appear on routing ladders and are usable **only by agents granted access**.
- **Grant access** to an agent (on the agent editor, the agent's **Model access** tab, or the model's **Access** dialog), stating what the agent should use the model for. A standard cloud model outside an agent's profile can be granted the same way; an explicit grant also bypasses the agent's `MaxTier`.
- The agent sees each granted model and its purpose in its prompt and uses it with `consult_model` (a one-off question) or `switch_model` (continue the current task on it, with tools). Administrators can also pin a queued task to a model.
- **Revoke** at any time; it takes effect on the agent's next AI call. Grant history is kept for audit.

### Task queue
Each agent works through its queue **top to bottom**. New tasks are inserted by priority (after the last task of equal or higher priority), or at the front when requested. On the agent's **Queue** tab administrators can drag tasks into any order, move them up/down/to the top, edit queued tasks (name, instructions, priority, purpose, pinned model, start time, retries), **hold** and **release** them, cancel them, re-run finished ones, and apply any of these to several tasks at once.

### Recurring tasks
**Add task** has a **Recurrence** section:

- **Repeat options:**
  - every N minutes, hours, days or weeks
  - daily at a time
  - weekly on chosen days at a time
  - monthly on a day (1-28) at a time
  - a custom cron expression
- **Ends:** never, on a date, or after a number of runs.
- **Overlap:** by default a run is skipped while the previous one is still queued or running.
- **Times** are the server's local time.
- **When it runs:** the first run happens at the chosen start (interval schedules run right away by default). Each run adds a normal task to the agent's queue.

The **Recurring tasks** card on the Queue tab shows every schedule (from the configuration or the dashboard) with its next and last run and run count. You can run one now, pause and resume it, or delete it (dashboard schedules only; configured ones can be paused). Dashboard schedules are kept when the configuration is reloaded.

### Global tasks
**Global task** (top bar, **Global tasks** page, or Ctrl+K) lets an administrator describe a goal once; the system does the rest:

1. **Plan.** A coordinator running on the free local model reads the request and every available agent's role, description, goals, tools and MCP access. It writes down what the request means and chooses the fewest agents that do the job well: a single step, parallel steps, or a pipeline where later steps depend on earlier ones (at most `GlobalTasks.MaxSteps` steps and the chosen number of agents). If no model is available or the plan is unusable, a rule-based plan sends the request to the best-matching agent.
2. **Run.** Each step is queued as a normal task with the right agent, in one shared workflow. Every step gets the whole request as you wrote it, besides its own instructions, so data the coordinator left out of a step (a scenario, a list of accounts) still reaches it. Steps whose dependencies are finished start automatically and receive the earlier results. A failed step does not stop the others. A step whose agent ends saying it could not do it (`complete_task` with `success=false`) counts as failed, not done. An agent works on one step at a time, so the coordinator gives steps that can run at once to different agents; a step pauses halfway through its time slice only when another task of its agent is waiting, and then lets it take its turn. When a step waits on its agent's AI usage limit or budget, or none of its models has worked for 30 minutes (providers out of credit or down), the administrators are told once. Every step knows its deadline (it is in its prompt, and the agent is reminded once when a fifth of its time, at least 15 minutes, is left).
3. **Combine.** When all steps are done, the host combines the results into one final answer (Markdown), or the agent you chose writes it. A single-step plan's result is the answer. With a chosen agent, a planned step that only compiles the report and waits on every other step is folded into the final step instead.

When a reviewer sends the answer back, the request is planned again with the feedback. What the earlier attempt got done (finished steps, and what unfinished ones got done) is kept: the coordinator plans only what is missing, every step and the final answer receive that work, and an attempt in which nothing new finishes still answers from it. A revision has what is left of the first deadline, and at least a quarter of the deadline; **Plan again** after a failure starts the clock over.

The options mirror **Add task**:

- name, instructions, priority, purpose, start (now, delay or time), recurrence, retries and starting tier
- **which agents may be used** (all, or a chosen set) and the **maximum number of agents**
- **who writes the final answer**
- a **deadline** (unfinished steps are then cancelled, keeping what they got done, and the answer uses what is ready). When an agent writes the final answer, the work steps stop 12% of the time earlier (15 to 60 minutes, at most a third), so the final step has time.
- **plan approval**: review the plan, then approve it or re-plan with feedback, before any agent starts

The detail page shows the understanding, each step's agent, status, instructions, result and timing, and the final answer. Under each step: its model turns, tokens and model time, its tool calls and how many repeated an earlier call exactly, how often it was paused and resumed, and its failed model calls. **Earlier attempts** keeps the steps of revisions the reviewer sent back. **Workflow timeline** shows every tool and model call of the run. Recurring global tasks create a fresh, newly planned run each time.

### Workflow timeline
A workflow page (`/correlations/{id}`, linked from tasks, global tasks and the activity feed) shows:

- the delegation tree
- messages between agents, if any. When an agent worked alone there are none, and the page says so.
- a **timeline** of everything that happened, filterable by type:
  - tasks created, started and finished
  - every tool call with its input and result
  - every model call with model, tokens, cost and reason
  - subagents and messages

### Reviews, Results and attachments

Agent tasks and global tasks (including recurring ones) have an **Outcome & review** section.

**Show me the result in Results.** The finished work goes to the **Results** page (the sidebar shows how many are new). Each result opens on five tabs:

- **Answer:** the result in Markdown, plus any structured data
- **Files:** every file the workflow produced, in a browser with previews of images, text, code, Word, Excel, PowerPoint, PDF and ZIP, and downloads. The files come from artifacts the workflow's tasks created and from the shared storage folder `results/{workflow}/`, which agents are told to use for deliverables. **Download all** gives one ZIP.
- **Conversation:** the messages between agents, and each agent's reasoning conversation with its tool calls
- **Review:** the reviewer's verdict, score and feedback
- **Agents & rewards:** who took part, their balances, and award fields

From a result you can:

- **Rate it** with 1-5 stars and a comment. Every agent that took part remembers it.
- **Award money** to the agents. Awards are profit credits: they count toward ROI and the learning system, are idempotent per submit, and revive a dead agent.

**Checked by (reviewer).** An agent, usually the manager, checks the work before it counts as done:

1. When the worker finishes, the reviewer gets a review task. It contains the instructions, the result, the files (shared with it) and the attachments.
2. It also contains the **expected outcome**, which is only ever shown to the reviewer. It is not in the worker's prompt, its task JSON or any agent tool, and the reviewer is told never to quote it.
3. The reviewer approves or sends the work back with feedback. The worker then gets a revision task with that feedback, up to **Revisions allowed** times. After the last revision the verdict is final.
4. For a global task, the reviewer checks the final answer. "Revise" plans the whole request again with the feedback.

The worker remembers each verdict.

**Attachments.** You can attach any files: documents, spreadsheets, PDFs, images (PNG, JPG, ICO…), source code (.cs, .vb, .js, .py…) and ZIP archives, up to `Limits.MaxUploadMb` (default 50 MB) each.

- The host extracts the text once, on upload. This covers Word, Excel (sheets as tab-separated rows), PowerPoint, OpenDocument, PDF text, RTF, ZIP listings with the text of the files inside, code and text, and approximate text from legacy .doc/.xls.
- Each agent gets every file as an artifact (`read_artifact` returns the text; `analyze_image` looks at images, and an icon's largest PNG is used) and as a copy in the shared storage under `attachments/`.
- The prompt lists the files with their text, up to 40,000 characters shared between them.
- For a global task, the planner sees what is attached, and every agent that gets a step receives the files.

### Isolation

- Agent runtimes and contexts are `Friend` types held privately by `AgentManager`. The outside world only receives immutable snapshots.
- Tools get an `AgentScope` of facades bound to one agent at construction: memory, communication client, artifacts, budget, own queue, approvals, vision and images. **No method on them takes an agent id**, so an agent cannot address another agent's data. Unit tests enforce this.
- Private memory is visible only to its owner, and to administrators in the dashboard.
- Artifacts belong to their owner. Another agent can read one only after the owner attaches it to a message, which records a grant. Grantees cannot re-share.
- The model only sees tools it is permitted to use. Each communication tool's `recipient` parameter is an enum of exactly the permitted agents. Every tool call is re-checked by the host, validated against its schema, bounded in output size, and recorded.
- Content from other agents, tools, web results and images is wrapped in `<untrusted source="…">` blocks, and the safety rules tell the model to treat it as data, not instructions.

### Communication and delegation
| Tool | Mode |
|---|---|
| `send_agent_message` | Fire-and-forget information or notification. The recipient stores it in its own memory. |
| `request_agent_help` | Request/response. Waits up to `InlineWaitSeconds`, then **parks** the task (`Waiting`). The agent keeps working on other tasks, and the parked task resumes when the reply arrives or the deadline passes. |
| `delegate_task` | Creates a task in the recipient's queue with `ParentTaskId`, `ParentAgentId`, `DelegatingAgentId`, `CorrelationId` and `DelegationDepth`. The result comes back as a structured `TaskResult` (`{success, summary, data, warnings}`). |
| `reply_to_agent` | Explicit reply. Replies are normally sent automatically when a message-triggered or delegated task completes. |
| `broadcast_notification` | Notification to a configured group. Requires `CanBroadcast`. |

**Loop protection.** Every message and task carries its *ancestry*: the agent path that led to it. A request or delegation to an agent already in that path is rejected (`LOOP_DETECTED`), so A→B→C→A stops at C→A. The other limits are:

- `MaxDelegationDepth` and `MaxCommunicationChainLength`
- `MaxChildTasksPerTask` and `MaxMessagesPerCorrelation`
- per-agent rate limits for messages/minute, requests/minute and delegations/hour
- `MaxOutstandingRequests`, `MaxMailboxSize` and message TTLs

A rejected message is stored with its reason and shown on the Communications page, and the sending model receives the error.

**Idempotency.** Each message has a unique `(sender, idempotency key)`. For messages sent by tools, the key is derived from the task and the model's tool-call id. Message→task creation is unique per message. After a crash, a task resumes from its checkpoint. A tool call that was in flight is only re-run if the tool is idempotent; otherwise the model is told the outcome is unknown.

### Human in the loop
`ask_human` creates a question in the dashboard **Inbox**. MCP tools listed in `ApprovalRequiredTools` require approval before every call. In both cases the task waits until someone decides: an administrator, or (see [Autonomy](#autonomy-agents-without-a-person-in-the-loop)) the agent's policy or its supervisor agent. The Inbox shows who decided each request (a person, `policy`, a supervisor agent or `time-out`); requests a supervisor agent is deciding are listed apart, and a person can still decide them first.

### Autonomy: agents without a person in the loop
Each agent has an autonomy **level** (agent editor → *Autonomy*, or `Autonomy` in its profile). Empty settings inherit from the base profile, then from the level's defaults.

| Level | What changes |
|---|---|
| `supervised` (default) | People decide everything the agent asks for. Every agent can keep a plan (`update_plan`); agents that can search the web can also read pages (`read_web_page`). |
| `assisted` | Small budget requests are approved at once (`Autonomy.AssistedBudgetUsd`, default $2, at most `AutoBudgetPerDayUsd` a day). The agent takes **initiative** when idle and directs itself: `set_goal`, `update_goal`, `schedule_task`, `list_schedules`, `cancel_schedule`, `create_plan`, `create_webhook`, `list_webhooks`, `delete_webhook`, `run_code`, plus the standard autonomy tools (`request_budget`, `notify_human`, skills, …). |
| `autonomous` | As assisted, and: budget up to `AutonomousBudgetUsd` (default $10) at once, tool approvals decided by its supervisor (or, without one, approved by its policy), its own behaviour changes applied at once (then checked by its evaluation), its hires approved within `MaxHires`, questions nobody answers within `AskHumanTimeoutMinutes` (default 240) go to its supervisor or are answered with "decide yourself", and one self-check pass before a result counts. |

```jsonc
"Autonomy": {
  "Level": "autonomous",
  "SupervisorAgentId": "ManagerAgent",   // decides what the policy does not, instead of a person
  "AutoApproveBudgetUsd": 5, "AutoApproveBudgetPerDayUsd": 20,
  "AutoApproveTools": ["deploy_site"],   // approved at once; ["*"] = all; [] = none (default with a supervisor: [])
  "AutoApplyBehaviorChanges": true, "CanHire": true, "MaxHires": 2, "AutoApproveHires": true,
  "Manages": ["ResearchAgent"],          // "*" = every agent of the same owner; hires are always included
  "AskHumanTimeoutMinutes": 120, "InitiativeMinutes": 180, "VerifyPasses": 1,
  "AllowedHttpHosts": ["api.example.com", "*.example.org"],   // http_request; "*" = any public host
  "KnowledgeBases": ["*"],               // search_knowledge
  "RunCode": true, "DeniedTools": ["create_webhook"]
}
```

- **Approval policy and supervisors.** A new request is first decided by the agent's policy (within its limits) or sent to its supervisor agent as a decision task (`{"decision": "approve" | "reject" | "escalate", "answer": …}`); otherwise it waits for a person. A supervisor that fails or does not decide within `SupervisorTimeoutMinutes` (60) hands the request back to people (with a notification). What a decision does — a budget credit, a behaviour change, a new hire — is applied whoever decided it.
- **Initiative.** An assisted or autonomous agent that has been idle for `InitiativeMinutes` gets an initiative task: review its goals (and team), do the most valuable next step or plan it, or say there is nothing worth doing. At most `MaxInitiativesPerDay` (6) a day.
- **Team.** An agent that manages others (`Manages`, or its hires) gets `team_status`, `assign_task` (optionally with a bounty from its wallet, its own review of the result, and waiting for it) and `cancel_agent_task`. With `CanHire` it can `hire_agent` (a helper with at most its own models, tools, MCP servers and level; it answers to its hirer and starts with money moved from the hirer's wallet) and `retire_agent` (the rest of the money goes back).
- **Bounties.** A task can carry a reward paid into the worker's wallet when its work is accepted (finished successfully, and approved if reviewed): set in *Add task*, a recurring task or a global task (shared by the agents whose steps succeeded) — this needs *Manage money* — or offered by agents through `delegate_task` / `assign_task` (`bounty_usd`, paid from their wallet). Bounties count as profit. Limits: `BountiesEnabled`, `MaxBountyUsd`.
- **Doing things.** `read_web_page` (public pages only), `run_code` (Python, Node or bash in the code sandbox), `http_request` (only `AllowedHttpHosts`; a host named exactly may be on your network, wildcards must resolve to public addresses; `{{secret:NAME}}` in the URL, headers or body is filled from the vault and never shown to the model, even when an API echoes it), `list_secrets`, `search_knowledge` (the agent's knowledge bases, limited to its owner's).
- **Secrets** (dashboard → *Secrets*, administrators): values encrypted with the host's `secrets.key`, shared with chosen agents, never returned by the API, kept out of logs. Bind each secret to the hosts that need it (*Only sent to*): `http_request` fills it in only for requests to those hosts, so even an agent tricked by a web page cannot send it anywhere else. A secret without hosts goes to any host its agents may call.
- **Plans and parallel tools.** `update_plan` keeps the task's plan in its checkpoint (shown with the task and kept visible after older messages are dropped). When one model reply calls several read-only tools (memory, artifacts, web search and pages, knowledge, lists, budget, team status), they run at the same time (`ParallelTools`, `MaxParallelTools` 4).
- **Evaluations and rollback** (agent → *Autonomy* → *Evaluation*). Fixed test cases with an expected outcome only the judge sees; a judge model (`Learning.ModelKeys`, else a local model) grades each result 1-10 (pass: 7). The first run is the baseline; after every approved behaviour change the agent runs its cases again, and a change that lowers the average by more than `EvalRollbackMargin` (0.5) or passes fewer cases is rolled back (a change a person approved is only reported). Test cases run as ordinary tasks: write ones without side effects.
- **Learning from failure.** `FailureStudyThreshold` (3) failed tasks in 24 hours open a case study of the agent (with organizational learning on), so its failures become lessons.
- **Email** (agent → *Autonomy* → *Mailboxes*, administrators). New mail in an IMAP mailbox becomes a task for the agent, with the email as untrusted data; only `AllowedSenders` (addresses or `@domains`) if set. The first check only notes where the mailbox stands. The password is a vault secret.
- **Goals, behaviour changes, webhooks, skills.** Also the ones agents set up for themselves are listed on the agent's *Autonomy* tab; administrators add goals and webhooks and roll back behaviour changes there.

Host settings (`Autonomy` section): `PoliciesEnabled` (off: every request waits for a person), `SweepSeconds` (60), `SupervisorTimeoutMinutes`, `AssistedBudgetUsd`, `AutonomousBudgetUsd`, `AutoBudgetPerDayUsd`, `AskHumanTimeoutMinutes`, `InitiativeEnabled`, `InitiativeMinutes`, `MaxInitiativesPerDay`, `MaxSelfSchedules` / `MaxSelfGoals` / `MaxSelfWebhooks` (10/10/5), `BountiesEnabled`, `MaxBountyUsd` (25), `HiringEnabled`, `MaxHiresPerAgent` (3), `EvalsEnabled`, `EvalAfterBehaviorChange`, `EvalTimeoutMinutes` (90), `EvalRollbackMargin`, `FailureStudyThreshold`, `ParallelTools`, `MaxParallelTools`, `HttpTimeoutSeconds` (30), `MaxHttpResponseChars` (20000), `EmailEnabled`, `MaxEmailChars` (12000).

---

## 6. MCP servers

MCP servers are added in `McpServers[]` and use the official MCP C# SDK. No code changes are needed.

```jsonc
{ "Id": "code-exec", "Name": "Code execution (Open Terminal)", "Enabled": true, "Transport": "Http",
  "Endpoint": "http://192.168.1.20:8004/mcp", "HttpMode": "StreamableHttp", "TimeoutSeconds": 180,
  "Auth": { "Type": "Bearer", "Secret": "<token>", "SecretEnvVar": "MCP_CODE_EXEC_TOKEN" },
  "ExcludeTools": ["read_user_terminal", "send_user_terminal_input", "display_file"],
  "IdempotentTools": ["list_files", "read_file", "grep_search", "list_processes", "get_process_status"],
  "DefaultArguments": { "run_command": { "wait": 60 }, "get_process_status": { "wait": 30 } } },
{ "Id": "project-a", "Enabled": true, "Transport": "Stdio",
  "Command": "C:\\McpServers\\ProjectA\\ProjectA.exe", "Arguments": [],
  "Auth": { "Type": "Header", "HeaderName": "X-Api-Key", "SecretEnvVar": "MCP_PROJECT_A_KEY" },
  "ApprovalRequiredTools": ["delete_record"] }
```

Then grant access per agent with `AllowedMcpServers` (and optionally `AllowedMcpTools` / `DeniedMcpTools`).

`DefaultArguments` adds arguments to a tool's calls when the model leaves them out (per tool name, or `"*"` for every tool of the server); only arguments the tool's schema declares are added. Open Terminal's `run_command` returns `status: running` at once unless it gets `wait`, and models then re-run the command or poll `get_process_status` with a garbled process id until they run out of turns; with `wait` the output comes back in the same call.

`HealthCheck` is a watchdog for a server: the host calls `Tool` with `Arguments` every `IntervalSeconds` (and at once after `TimeoutsBeforeCheck` call timeouts in a row) and expects `Expect` in the answer. After `FailuresBeforeRestart` failed checks the server is "not responding": agents' calls to it fail at once with a clear message instead of each waiting `TimeoutSeconds`, the connection is reset (a new session), and when that does not help `RestartCommand` (if set) runs on this host — for example an `ssh` command that restarts the sandbox container. One passing check (every `DownIntervalSeconds` meanwhile) ends it. The code sandbox uses `run_command` `echo ok`.

### Web search

Web search is built in rather than an MCP server. Agents with the `search_web` tool (every agent by default, and subagents) and chats with **Web search** on ask these services in order, the next one when a service fails or finds nothing:

1. **Brave**: the Brave Search API, when `BraveApiKey` is set.
2. **Bing**: Bing's results as RSS; no key needed. News searches use Bing News, limited to the past day, week or month.
3. **SearXNG**: your instance's JSON API (`UrlTemplate`, then the fallback `Endpoints`).

```jsonc
"WebSearch": {
  "Enabled": true,
  "Order": [ "brave", "bing", "searxng" ],      // the default; a service missing what it needs is skipped
  "BraveApiKey": "",                              // https://brave.com/search/api/
  "UrlTemplate": "http://192.168.1.20:8080/search?q={query}&format=json",   // {query} or <query> is replaced by the encoded query
  "MaxResults": 8, "MaxSnippetChars": 400, "TimeoutSeconds": 20, "MaxConcurrency": 4, "SafeSearch": 1
}
```

- **A failing service rests.** After 3 failed searches in a row a service is skipped for 5 minutes, unless every service is resting.
- **Bing's terms.** Bing's RSS results are meant for personal, non-commercial use. For anything else, use Brave (or take `bing` out of `Order`).

- **What agents can control.** Agents choose only the query and optional category, time range, language and page. The host fixed the URL, so the tool cannot be pointed at other hosts.
- **What agents get back.** Results (title, URL, snippet, engines), direct answers and an infobox. They are marked as untrusted web content.
- **JSON must be enabled in SearXNG.** SearXNG must allow the JSON format (`search.formats: [html, json]` in its `settings.yml`); otherwise it answers 403.
- **Quality depends on SearXNG's engines.** Results come from whichever engines the SearXNG instance has enabled.
- **Dashboard.** The **MCP servers & tools** page shows each service in order with its state, the agents that have the tool and a **Test a search** box (`GET /api/websearch`, `POST /api/websearch/test`).

- **Tool names.** Tools appear to the model as `mcp__<server>__<tool>`.
- **Descriptions loaded when needed.** An agent with many MCP servers used to send every tool's description with every model call (20-40k tokens for 8-11 servers). With `ToolLoading.Enabled` (on), a task starts with its host tools, a core set of MCP tools (`ToolLoading.CoreTools`, plus the agent's `RecentlyUsedTools` (8) most used ones over `RecentDays` (14)) and, in its system prompt, the names of the rest by server. It calls `load_tools` with names (`server:tool`) or a keyword query to load more; loaded tools stay offered for the rest of the task. An agent with little to defer (under `MinDeferredChars`, 6000) gets every tool at once, and tasks started before this keep every tool.
- **Isolation.** Each server connects, reconnects with backoff and is health-checked independently. A failing server only removes its own tools from agents' tool lists.
- **Images.** Image results from MCP tools are stored as artifacts that agents can inspect with `analyze_image`.
- **Authentication.**
  - `Auth.Type` is `Bearer` (sends `Authorization: Bearer <secret>`), `Header` (sends the secret in `HeaderName`, for example `x-api-key`) or `None`.
  - The secret comes from the environment variable in `SecretEnvVar` when that variable is set, otherwise from `Secret` in the file.
  - Secrets are redacted from logs and never shown in the dashboard.
- **Excluding tools.** `ExcludeTools` hides tools from every agent. The shipped `code-exec` (Open Terminal) entry hides the three tools that act on the human user's own terminal and file viewer.
- **Shipped servers.**

  | Id | What it is | Granted to |
  |---|---|---|
  | `code-exec` | Open Terminal on 192.168.1.20: Linux shell and file tools | ProcessingAgent |
  | `storage` | Built-in shared folder | all agents |
  | `shell` | Built-in PowerShell/cmd on the Agentic host; each command needs approval in the Inbox | ProcessingAgent |
  | `code-review` | Built-in automated code review | Manager, Analysis, Processing |
  | `browser` | Microsoft's Playwright MCP driving headless Chrome | Research, Design, Processing |
  | `gitea` | Your Gitea instance: the official gitea-mcp plus full REST API access (off until you add a token) | Manager, Processing |
- **Placeholders.** In a stdio server's `Command`, `Arguments` and `WorkingDirectory`, `{self}` is replaced by this executable's path and `{data}` by the data directory.

### Shared storage (built in)

`storage` is a shared folder (`data\shared`) where agents leave files for each other: one agent writes a report, another reads it. No extra software is needed. The host starts its own executable as a stdio MCP server:

```jsonc
{ "Id": "storage", "Name": "Shared storage", "Enabled": true, "Transport": "Stdio",
  "Command": "{self}", "Arguments": ["--mcp-storage", "{data}\\shared", "--max-file-mb", "20", "--quota-mb", "2048"],
  "IdempotentTools": ["list_files", "read_file", "search_files", "grep", "storage_info"] }
```

- **Tools.** `list_files`, `read_file` (text, optional line range), `write_file` (atomic, optional append), `make_folder`, `move`, `delete` (files and empty folders), `search_files` (by name), `grep` (by content) and `storage_info`.
- **Confinement.** Every path stays inside the shared folder: absolute paths, `..` and links are refused.
- **Limits.** The per-file size and total quota limits apply.
- **Access.** Grant it per agent with `AllowedMcpServers`. Agents without the grant don't see the tools.

### Shell commands (built in)

`shell` runs PowerShell or cmd commands on the machine that hosts Agentic (`--mcp-shell {data}\workspace`).

- **Tools.** `run_command(command, shell, folder, wait_seconds)`, `run_command_background(command, shell, folder)`, `command_output(id, wait_seconds)`, `kill_command(id)` and `shell_info`.
- **No time limit on commands.** `run_command` waits up to `wait_seconds` (default `--timeout`, at most 600) for a command. One still running then is not stopped: it goes on in the background and the agent gets its id and the output so far, so it is never stuck behind a command that hangs. `run_command_background` starts one there at once (servers, watchers, long builds). `command_output` audits it: running or finished (exit code), for how long, how long since it last printed something, its new output, and a warning when it seems to wait for input (a question like `(y/n)`) or has printed nothing for 2 minutes; `wait_seconds` waits for it to finish or print more. `kill_command` stops it with everything it started. A call also returns as soon as the shell ends, even when something the command started (a server) keeps its output open.
- **Working folder.** Commands start in the workspace folder; `folder` must stay inside it.
- **Secrets.** Environment variables that look like secrets (`AGENTIC_*`, `MCP_*`, names containing KEY, TOKEN, SECRET or PASSWORD) are removed before a command runs.
- **Limits.** Output is capped at `--max-output-kb` per result. Each command runs in a Windows job object of its own (nested in one that ends with the shell server), so `kill_command` also stops what outlived its shell, and nothing outlives a restart of Agentic.

Commands run as Agentic's Windows account and can read anything that account can, including `appsettings.json`. The shipped config therefore requires an administrator's approval for every `run_command` (`ApprovalRequiredTools`); `run_command_background` always needs the same approval as `run_command`, and in chats `command_output` and `kill_command` are only offered to those who may run commands there. Remove that line only for agents you trust with full access to the machine.

### Code review (built in)

`code-review` (`--mcp-code-review {data}\shared`) provides deterministic checks that agents combine with their own reading:

- `review_code` and `review_file` (a file in the shared storage) return findings with severity and line numbers, plus metrics:
  - hard-coded secrets and keys
  - SQL built by concatenation or interpolation
  - eval/exec and shell injection
  - unsafe deserialization and weak crypto
  - disabled TLS validation and XSS sinks
  - swallowed exceptions, debug leftovers and TODOs
  - long lines, deep nesting and duplication
- `diff_code` returns a line diff and only the findings the change introduces.
- `review_checklist` returns per-language review checklists.

### Web browser (Playwright)

`browser` is Microsoft's official [`@playwright/mcp`](https://www.npmjs.com/package/@playwright/mcp) (0.0.83). It needs Node 20 or later, so a portable Node 22 and the package are installed in `Agentic\tools\`, and the browser is the installed Google Chrome. The server runs with `--headless --isolated`; screenshots go to `data\browser`.

- **Tools.** 25 tools: navigate, snapshot (accessibility tree), click, type, fill forms, select, hover, press keys, tabs, screenshots, console and network logs, evaluate JavaScript, and more.
- **One shared browser.** All agents share it, and `MaxConcurrency: 1` serializes their calls.
- **Network reach.** Agents with the browser can reach anything the host can reach, including the LAN.
- **Why not Puppeteer.** The official Puppeteer MCP server is deprecated, which is why Playwright is used.
- **Sample server.** `samples/EchoMcpServer` is a VB.NET stdio server built on the same SDK. Use it as a template for company servers; the tests also use it.

### Gitea (built in)

`gitea` gives agents everything a GitHub MCP server does, on your own Gitea (`https://gitea.example.com`). Agentic runs one server (`--mcp-gitea`) that contains two parts:

- **The official [gitea-mcp](https://gitea.com/gitea/gitea-mcp)** (1.8.0, in `Agentic\tools\gitea-mcp`). Its 57 tools are passed through unchanged. They cover:
  - repositories (create, fork, search, list), files (read, tree, create/update/rename/delete in one commit), branches and commits
  - tags and releases
  - issues (comments, labels, dependencies), pull requests (create, update, merge, diff, files), reviews and review comments
  - labels, milestones, projects, wiki, packages, time tracking, notifications
  - Actions: workflows, runs, logs, artifacts, dispatch, rerun, secrets and variables
  - user, org and team search
- **REST API tools** for everything else in Gitea's API (482 endpoints on 1.27): repository settings, archive and transfer, webhooks, branch protection, collaborators, teams and organizations, deploy and SSH keys, topics, mirrors, commit statuses, stars and admin.
  - `api_find_endpoint` searches the instance's own API spec (`/swagger.v1.json`) and returns the method, path, parameters and JSON body fields.
  - `api_get`, `api_write` (POST, PUT or PATCH) and `api_delete` then call the endpoint.
  - Paths are relative to `/api/v1`. Requests can't leave the configured host, so the token is never sent anywhere else.

**Setup:**

1. In Gitea, create a user for the agents (for example `agentic-bot`). Give it access to what the agents may work on: add it as a collaborator, or put it in an organization team with Write or Admin permission.
2. Signed in as that user, open **Settings → Applications** and generate an access token. Grant **Read and Write** for `repository`, `issue`, `organization`, `user`, `notification`, `package` and `misc`. Add `admin` only if the account is a site administrator and agents should manage users.
3. In `appsettings.json`, paste the token into the `gitea` entry's `Auth.Secret` and set `"Enabled": true`. Alternatively, set the `GITEA_ACCESS_TOKEN` environment variable instead of using `Secret`. Restart Agentic.

**How it is configured:**

- **Token handling.** The token reaches the server as the `GITEA_ACCESS_TOKEN` environment variable, never on its command line, and it is redacted from logs and the dashboard.
- **Approvals.** Permanent deletions wait for an administrator's approval in the Inbox: `delete_branch`, `delete_tag`, `delete_release`, `package_write` and `api_delete`. Edit `ApprovalRequiredTools` to change this.
- **Optional settings.** In `EnvironmentVariables`, `"GITEA_READONLY": "true"` exposes only read tools, `"GITEA_TOOLS"` / `"GITEA_SCOPES"` limit the tools further, and `"GITEA_INSECURE": "true"` accepts a self-signed certificate.
- **Per-agent limits.** To give an agent read-only access, combine `AllowedMcpServers: ["gitea"]` with one `AllowedMcpTools` rule per tool, such as `gitea:issue_read` and `gitea:pull_request_read`. Rules name whole tools; wildcards inside a tool name are not supported.
- **Recovery.** If gitea-mcp exits, the call in progress fails and the next call starts it again.
- **File contents as text.** `get_file_contents` answers with base64; Agentic decodes text files before an agent or chat sees them (`"encoding": "utf-8 (decoded from base64 by the host)"`), for this and any other MCP server. Binary files stay base64.
- **Not covered.** Uploading binary files (release assets, issue attachments) is not supported. Use the `git` server for clone and push work.

### More built-in agent tools

Twelve more servers are built into Agentic.exe (`"Command": "{self}"`). Each one is an ordinary entry in `McpServers`, granted per agent with `AllowedMcpServers`. Tools marked below as needing approval wait for an administrator in the Inbox (`ApprovalRequiredTools`); remove a name there to let agents use that tool freely.

| Id | Switch | Tools | Needs approval |
|---|---|---|---|
| `utilities` | `--mcp-utilities` | `current_time`, `convert_time` (IANA, Windows, abbreviations, offsets), `date_add`/`date_diff` (business days), `calculate` (exact decimal math), `convert_units`, `hash_text`, `encode_text` (base64, URL, hex, HTML, JWT), `new_uuid`, `regex_test`, `generate_password`, `random_secret`, `random_number`, `random_pick`, `explain_cron`, `text_stats`, `text_diff`, `transform_lines`, `extract_patterns`, `convert_case` | - |
| `git` | `--mcp-git --roots "{data}\workspace;{data}\shared"` | `git_status`, `git_diff`, `git_log`, `git_show`, `git_blame`, `git_grep`, `git_branches`, `git_switch`, `git_add`, `git_commit`, `git_restore`, `git_stash`, `git_merge`, `git_tag`, `git_clone`, `git_pull`, `git_fetch`, `git_push`, `git_init` | `git_push` |
| `documents` | `--mcp-documents --roots "{data}\shared;{data}\workspace"` | `read_document` (PDF, .docx, .xlsx, .pptx, CSV, .eml, HTML, text), `document_info`, `search_document`, `create_document` (Markdown to .docx/.pdf/.html/.md), `create_spreadsheet` (.xlsx/.csv), `convert_document` | - |
| `database` | `--mcp-database --roots ...` | `list_databases`, `list_tables`, `describe_table`, `query` (read-only), `explain_query`, `execute`, `export_query` | `execute` |
| `email` | `--mcp-email --roots "{data}\shared"` | `send_email`, `list_emails`, `read_email`, `list_email_folders`, `mark_email` | `send_email` |
| `netops` | `--mcp-netops --known-hosts "{data}\ssh_known_hosts"` | `ping`, `dns_lookup`, `check_ports`, `check_tls`, `http_check`, `traceroute`, `ssh_hosts`, `ssh_run` | `ssh_run` |
| `deps` | `--mcp-deps --roots ...` | `package_info`, `package_versions`, `check_vulnerabilities` (OSV.dev), `audit_dependencies` | - |
| `docker` | `--mcp-docker` | `docker_info`, `list_containers`, `inspect_container`, `container_logs`, `container_stats`, `list_compose_projects`, `list_images`, `control_container`, `remove_container`, `pull_image`, `exec_in_container` | the last four |
| `windows` | `--mcp-windows` | `system_overview`, `list_services`, `control_service`, `event_log`, `list_processes`, `end_process`, `scheduled_tasks`, `network_connections` | `control_service`, `end_process` |
| `data` | `--mcp-data --roots ... --node ... --mermaid-cli ... --chrome ...` | `validate_data`, `format_data`, `convert_data` (JSON, YAML, TOML, XML, CSV), `query_data` (JSONPath), `validate_schema` (JSON Schema), `diff_data`, `statistics`, `aggregate` (group by), `percent_change`, `compound_interest`, `loan_schedule`, `cash_flows` (NPV, IRR, payback), `make_chart` (.svg/.png), `render_mermaid` | - |
| `files` | `--mcp-files --roots ...` | `archive_list`, `archive_extract`, `archive_create` (zip, tar, tar.gz), `file_hash`, `file_info` (real type from the bytes), `image_info`, `image_transform` (resize, crop, rotate, convert), `ocr` (images and scanned PDFs) | - |
| `lookups` | `--mcp-lookups` | `public_holidays`, `business_days` (skips holidays), `currency_convert` (ECB rates), `ip_lookup`, `whois` (RDAP) | - |

- **Folders.** `--roots` lists the folders a server may touch (separated by `;`). A relative path starts in the first one. `..`, other folders and links that lead outside are refused.
- **git.** It runs `git.exe` directly (no shell) with prompts off, and refuses arguments that start with `-` (option injection) and force pushes. For repositories on the Gitea host it sends the `gitea` bot token as an HTTP header; the token never appears in output. Commits use the repository's identity, or `GIT_USER_NAME` / `GIT_USER_EMAIL`.
- **documents.** It reads PDFs with PdfPig, and Office files directly from their XML. It writes .docx, .xlsx and .pdf itself, with no Office install. PDFs use the standard Helvetica and Courier fonts, so characters outside Windows-1252 show as `?`. Scanned PDFs have no text layer and need OCR.
- **database.** Add databases as `DB_<NAME>` environment variables: `"<sqlite|postgres|mysql|sqlserver>:<connection string>"`. `DB_<NAME>_WRITABLE: "true"` lets `execute` change one. `query` accepts only single read-only statements, and they run read-only at the database too (a read-only SQLite connection, a read-only transaction on PostgreSQL and MySQL, and a rollback everywhere). SQLite files in the folders can be queried by path. Passwords are hidden in `list_databases`.
- **email.** Off until configured: `EMAIL_ADDRESS`, `EMAIL_USER`, `EMAIL_SMTP_HOST`/`PORT` (STARTTLS on 587; 465 is not supported), `EMAIL_IMAP_HOST`/`PORT` (TLS on 993) and the password in `Auth.Secret`. `EMAIL_ALLOWED_RECIPIENTS` (addresses or `@domains`) limits who can receive mail, and `EMAIL_MAX_PER_HOUR` (20) limits how much. Mail bodies reach the model marked as untrusted.
- **netops.** SSH hosts go in `SSH_HOSTS` (`"box=root@192.168.1.20:22, web=deploy@web1"`). Their credentials go in `SSH_KEY_<NAME>` (a key file path or PEM text) or `SSH_PASSWORD_<NAME>`. A host's key is remembered the first time it is seen, and a changed key is refused. Record types other than A/AAAA/PTR come from DNS over HTTPS (`NETOPS_DOH_URL`, default Cloudflare).
- **docker.** Off until configured. Set `DOCKER_HOSTS` (`"box=tcp://192.168.1.20:2375"`, `"local=npipe:////./pipe/docker_engine"` or `unix://`). It talks to the Engine API directly, so no docker CLI is needed. `inspect_container` shows environment variable names only, never their values. Expose an engine's TCP port only on a private network: it gives full control of that host.
- **windows.** It runs fixed commands only (`wevtutil`, `schtasks`, `netstat` and CIM queries), with no shell input from the model. System processes and Agentic itself can't be ended; `WINDOWS_PROTECTED_PROCESSES` adds more names.
- **utilities.** Passwords, secrets and random numbers come from the operating system's cryptographic generator. `explain_cron` uses the same Cronos library as Agentic's schedules. Token counts are estimates.
- **data.** Inputs can be text or a file in the folders. Money math uses decimals, so cents stay exact. Charts are drawn as SVG and turned into PNG with SkiaSharp. Mermaid diagrams use `mermaid-cli` in `Agentic\tools\mermaid-cli`, run by the portable Node with the installed Chrome. To reinstall it, run `npm install @mermaid-js/mermaid-cli` there with `PUPPETEER_SKIP_DOWNLOAD=1`.
- **files.** Extraction refuses entries that would land outside the target folder (zip slip), refuses links, and stops past 2 GB. Images are handled by SkiaSharp. OCR runs Tesseract when it is installed (found in `C:\Program Files\Tesseract-OCR`, or set `--tesseract`/`TESSERACT_PATH`). For scanned PDFs it reads the page images; JBIG2 images can't be decoded.
- **lookups.** It calls free public services that need no key: date.nager.at, frankfurter.dev (ECB rates), ipwho.is and rdap.org. Each can be pointed elsewhere with `LOOKUPS_HOLIDAYS_URL`, `LOOKUPS_RATES_URL`, `LOOKUPS_IP_URL` and `LOOKUPS_RDAP_URL`. Private IP addresses are answered locally and never sent out.

---

## 7. Configuration

`appsettings.json` allows comments; a fully commented example is `appsettings.example.json`. Any value can be overridden with environment variables prefixed `AGENTIC_`, using `__` for nesting (for example `AGENTIC_Server__Port=9090`).

> **Renamed from AgentHost.** Variables with the old `AGENTHOST_` prefix still work; `AGENTIC_` wins when both are set. On first start, an existing `data\agenthost.db` (with its `-wal`/`-shm` files) is renamed to `data\agentic.db`. Old `agenthost-*` logs and backups age out with the normal retention. Prometheus metric names changed from `agenthost_*` to `agentic_*`, and the session cookie is now `agentic_session`, so you sign in again once.

**Local models.** `LocalModels.Enabled` (true) says whether this server runs its own local models (`local-main`, `local-vision`); `false` drops them and every reference to them, and agents use the `Default` ladder's first model. `LocalModels.Slots` (4) is how many requests each local model server works on at once, and `LocalModels.SlotsByModel` sets one model's. Both work from the environment: `AGENTIC_LocalModels__Enabled=false`, `AGENTIC_LocalModels__Slots=2`. Details: [SETUP.md](../SETUP.md#43-local-models).

**Docker.** `Dockerfile` and `docker-compose.yml` run the server on Linux; the published image is `ghcr.io/smurflabs/agentic`. See [SETUP.md](../SETUP.md#11-docker).

**Secrets.** Environment variables are preferred, but the file may also hold secrets when that suits the deployment. Secrets in the file are redacted from logs and the dashboard.

- The AI key goes in `AGENTIC_AIProviders__OpenWebUI__ApiKey`, or in `AIProviders.OpenWebUI.ApiKey`.
- MCP secrets come from the environment variable named in `Auth.SecretEnvVar`, or from `Auth.Secret`.
- API tokens are stored as SHA-256 hashes, or read from `TokenEnvVar`.
- The alert webhook URL is read from `Alerts.WebhookUrlEnvVar`.

| Section | Purpose |
|---|---|
| `Host` | Instance name, data directory, shutdown timeout, `MaxAgents` (50) |
| `Server` | Bind host/port, request limits, session length, SSE client limit |
| `Admin` | Users (PBKDF2 hashes), API tokens (scopes `admin` / `read` / `submit-task`, optional agent allowlist) |
| `AIProviders` | One or more OpenAI-compatible endpoints |
| `Models` | Models with capabilities, purposes, tier, limits, `MaxConcurrency`, pricing, fallbacks, circuit breaker |
| `Routing` | Ladders, spillover, escalation policy |
| `Vision`, `ImageGeneration`, `VideoGeneration` | Vision models; the images and video APIs (`BaseUrl`, `ApiKey`, models in order), per-image and per-second prices, daily image quota, video length and resolution |
| `Economy` | Default balance, global daily cloud cap, fail-closed pricing, runway window and low-runway threshold |
| `Subagents` | On/off, concurrent per agent and host-wide, per-task limit, default and maximum spend cap, timeouts, record retention |
| `GlobalTasks` | On/off, planner models (local only), maximum steps, default deadline, planning timeout |
| `Learning` | On/off, analysis model (local only), broke threshold, success thresholds (`MinRoi`, `MinProfit`, `LargeProfit`), cooldowns, evidence window, `RequireApproval`, lessons per study, lessons per prompt |
| `McpServers` | See above |
| `Chat` | Chat tab: on/off, default model, system prompt added to every chat, `TaskModel` (titles and search queries; empty = the chat's model), code sandbox MCP server (`CodeServerId`), `AllowLocalCodeExecution` (administrators only; off by default), code timeout, `MaxToolRounds` (24; 50 for the CLI, desktop app and VS Code: how often an answer's progress is checked, not a stop - an answer whose tool calls keep bringing new results goes on, one whose last stretch brought nothing new is asked to answer; `MaxTotalToolRounds`, 0 = none, is a fixed total; a chat's `/limits` is a stop), `MaxWebSearches` (30) / `MaxPageReads` (60) per answer, history messages sent, `ClientToolTimeoutSeconds` (14400 = 4 h: the longest wait for a tool on the user's computer; no shorter limit while that computer stays connected), `ApprovalTimeoutSeconds` (900: how long a call waits for the user's approval), `AutoCompactPercent` (80), `DefaultPermissionMode` (auto), `ContextWindows`, `Sandbox` (download files out of the code sandbox, and stop long-lived sandbox processes/containers the host started; app previews at /container/{id}/), `MaxParallelSubagents` (6) / `MaxParallelTools` (8) / `SubagentMaxRounds` (50) for parallel agents |
| `WebSearch` | Built-in `search_web` tool and chat web search: service order (Brave, Bing, SearXNG), Brave key, SearXNG URL template, result count, snippet length, timeout, concurrency, safe search |
| `Communication` | All messaging limits and groups |
| `Limits` | Global concurrency for AI, MCP, messages and the DB; artifact size and quota |
| `Supervision` | Auto-restart, backoff, unhealthy threshold, stuck detection |
| `Retention` | Day counts per data type (including `CaseStudyEvidenceDays`), backups |
| `Logging`, `Alerts` | Log level, directory and rotation; webhook for important events |

On startup, validation catches the following, and **nothing starts** if any are found:

- duplicate ids
- unknown recipients, models, MCP servers or groups
- impossible model policies (no allowed model at or below `MaxTier`)
- capability/purpose mismatches
- invalid cron expressions and handler parameters
- plaintext passwords
- a missing API key

---

## 8. Security

- **Network exposure.** The admin server binds to `127.0.0.1` by default. For remote access, use a VPN, a private LAN, or an HTTPS reverse proxy (IIS ARR, Caddy, nginx on another box). Avoid exposing it to the internet directly.
- **Binding other addresses.** `HttpListener` needs administrator rights or a URL ACL to bind anything other than localhost: `netsh http add urlacl url=http://+:8080/ user=DOMAIN\svc-agentic`.
- **Native HTTPS.** Set `Server.UseHttps: true` and bind a certificate to the port: `netsh http add sslcert ipport=0.0.0.0:8443 certhash=<thumbprint> appid={<any-guid>}`.
- **Sign-in.** PBKDF2-SHA256 password hashes (210k iterations). Server-side sessions use an HttpOnly, SameSite=Strict cookie. State-changing requests need a **CSRF token** header. Login attempts are throttled.
- **Responses.** Every response carries a strict CSP (no inline scripts or styles). Errors are returned as structured JSON without stack traces (unless `ExposeErrorDetails` is set). Request bodies and timeouts are limited.
- **Secret handling.** Secrets are redacted from logs and events. AI responses are not written to log files. With `StoreAiContent` off (the default), AI content isn't stored in the database either.
- **Audit.** Every administrative change goes to the audit log.
- **User accounts.** Administrators manage dashboard accounts under **Users & access**:
  - Each account is an **Administrator** (everything, including user management) or a **User** with chosen permissions and a set of agents. A User sees only those agents' pages, tasks, messages, activity, approvals, ledgers and artifacts.
  - The permissions are: **View workforce** (`agents.view`), control agents, manage tasks, global tasks, **Chat & model API** (`models.use`: Chat, custom models and `/v1`), **Chat tools & code** (`chat.tools`: MCP tools and running code from Chat), results, message agents, agent memory, create & edit agents, answer the inbox, view finance, manage money, manage learning, manage models, global operations, and view system. Existing users do not get the two chat permissions automatically: grant them under **Users & access**.
  - **View workforce** shows the Workforce (agents, subagents, learning, global tasks, tasks, communications, the inbox) and its dashboards (Overview, Operations, Console, Live events). Every permission that acts on agents includes it. A User with none of them, such as a chat-only account, sees no agents anywhere. The Workforce and Monitor pages are gone, their API routes answer 403, and other pages (Models, status) leave agents out. Home is then the first page the user may open.
  - **Models.** Each User account can be limited to chosen models (**Which models this user can use**: provider models and custom models). Models outside the list are not offered in Chat, the desktop app, the CLI or `/v1/models`, cannot be used through the API (403), and the user cannot give them to an agent.
  - **API keys** act with their account's permissions (see [API keys](#api-keys)).
  - The server enforces every permission on every API route; changes that aren't mapped to a permission are administrator-only. The dashboard also hides what a user cannot use.
  - Changing an account's role, permissions or status signs that user out so the change applies at once.
  - At least one enabled administrator always remains, and administrators cannot demote or delete themselves.
- **Passwords.**
  - Everyone can change their own password (user menu → **Change password**; the current password is required, and other sessions end).
  - Administrators can reset other users' passwords.
  - Passwords need at least 10 characters.
  - A password changed in the dashboard for a user defined in `Admin.Users` survives restarts until the hash in the config file itself changes.
- **What agents can't do.** Agents cannot change permissions (theirs or others'), read other agents' memory, grant MCP access, change model policy, stop the host or edit configuration. No tool exists for any of these.
- **Downloads.** The host only downloads files from the configured AI provider's own origin, such as generated images, so agent-supplied URLs cannot be used for SSRF.
- **Admin-level key.** The OpenWebUI key used during setup is an **admin** key; it can read OpenWebUI's image config, including its upstream key. Create a dedicated non-admin OpenWebUI user for Agentic. Give it access to only the models above, plus image generation permission, and rotate the key that was shared during setup.

---

## 9. Dashboard and API

**Working in the console:**

- **Ctrl+K** opens the command palette. Jump to any page or agent (including an agent's queue, budget or "add task"), run commands (new agent, study an agent, pause/resume all, emergency stop, theme), or open a `tsk_…`/`cor_…` id. Use **/** to focus search.
- Tables with a **CSV** button export what is shown: agents, tasks, wallets, ledgers, lessons and the audit log.
- Pages update live. Values that change on a refresh flash briefly, and a top progress bar shows loading and saving.
- Light and dark themes are available.
- **Animations** follow the operating system by default. Windows Server turns system animations off, so browsers there report "reduced motion". Use the user menu (**Animations: auto / on / off**) to choose; the setting is remembered per browser.

Dashboard pages:

| Page | What it shows |
|---|---|
| Chat | Conversations with any provider or custom model: streaming, files, web search, MCP tools, code interpreter, runnable code blocks |
| Overview | KPIs with trend lines (agents alive and dead, running and queued work, tasks and AI calls in 24 h, local-model slots, spend vs. the daily cap, new results); charts of tasks finished (completed vs failed), AI calls, tool calls and messages per hour, cloud spend per hour and the last 14 days; a heat map of which agent works when; money and survival per agent; model usage; tasks by status; review verdicts; agent cards; "needs attention"; live feed |
| Operations | Everything running, waiting or held right now with cancel/release controls; AI and tool failure rates; average AI call and task duration per hour; AI reliability per hour; active subagents; queue load per agent; model capacity with p50/p95 latency and queue wait; work per agent; why each model was used (routing reason and purpose); message mix; tool reliability; failures in the last 24 h. **Pause all**, **Resume all** and **Emergency stop** (pause everything, cancel running tasks and subagents) |
| Agents | Searchable, filterable list with bulk Start/Stop/Pause/Resume/Restart, queue sizes, subagents, balances. **New agent** opens the form-based editor (also used to edit and clone). |
| Agent | Tabs: Overview, **Queue** (drag-and-drop order, edit, hold/release, bulk actions, add task with model pin and start time), History, **Subagents**, **Model access** (grants), Memory, Messages, Communications, Tools (exactly what the model sees), AI usage, Budget, **Learning** (its studies and lessons, Study now), Activity, Artifacts, Settings |
| Subagents | All subagents with status, models, spend vs. cap, results; cancel running ones |
| Learning | Case studies of broke and profitable agents (report, evidence, re-run), and the shared lesson library with search, filters, approve/retract/pin/edit |
| Tasks | All tasks with search (name, text, task or workflow id), agent/status filters, paging and bulk cancel/retry |
| Communications | Topology graph (traffic and permitted links), filterable message log |
| Global tasks | Requests the system planned and distributed: status, progress, agents, plan approval, final answers; recurring global tasks |
| Workflow | Delegation tree, messages and a filterable timeline of every task, tool call and model call for one correlation id |
| Inbox | Questions and approvals waiting for an administrator |
| Finance | Your account balance, agent wallets, ROI, spend by model, ledger, pricing editor |
| Model Square | The price of every model per 1M input and output tokens (administrators edit it) |
| Balance | Your account balance, spend and history; administrators see and top up every account |
| Models & access | Health, circuit breakers, slots, latency, spend, prices, who may use each model (profile vs. granted), the agent × model access matrix, custom model registration, ladders |
| Custom models | Routing models: one name, a model per kind of work, with fallbacks; usage; a tester |
| MCP | Server status and tools |
| Swagger | Interactive API reference |
| Live events | Events per minute (info, warning, error) for the last hour, event counts by type and by agent, KPIs with trend lines, a live feed you can filter by agent, type, level and text and pause, plus the persisted history |
| Results | Delivered results: inbox with New / To rate / Rated / Archived, ratings and approval rate; each result with Answer, Files (preview and download), Conversation, Review and Agents & rewards |
| Config | Validation results and the effective configuration, with secrets removed |
| Audit log | Every administrative change |

### Phones and tablets

Phones and tablets get the same app as the desktop, with every page and every Chat control. On screens up to 900px wide, `css/mobile.css` and `js/mobile.js` change the layout:

- **Top bar:** the menu button, the logo, search (the command palette), Global task, notifications and a round account button.
- **Sidebar:** opens as a drawer.
- **Bottom tab bar:** Chat, Overview, Agents, Inbox (with the count of waiting items) and Menu (all pages).
- **Tables:** each row becomes a card with labelled values.
- **Dialogs:** open as bottom sheets.
- **Chat:** the conversation list is a drawer that closes once you pick a chat. The composer keeps every control, and the layout follows the on-screen keyboard; the tab bar steps aside while you type.

On a touch screen, Enter starts a new line and the send button sends. Opening a chat does not pop up the keyboard.

**Desktop layout** in the account menu shows a phone the desktop site at desktop width. **Mobile layout** switches back. The choice is a cookie, so `?desktop=1` and `?mobile=1` (or `?auto=1`) in the address do the same.

### Chat

**Chat** (under Workspace) works like OpenWebUI.

- **Conversations.** The left column lists your conversations by date (pinned first). Search, open, rename, pin or delete them, or start a **New chat**. Conversations are private to their owner. A new conversation is named from its first exchange by a model (`Chat.TaskModel`, or the model that answered).
- **Models.** Pick any provider model (grouped by provider) or any custom model from the picker at the top. The last choice is remembered. The answer shows which model actually replied, and for a custom model, which route it took and why.
- **Composer.** Enter sends; Shift+Enter starts a new line (on a touch screen, Enter starts a new line and the send button sends). Attach files with **+**, by pasting, or by dropping them. Text files are read into the prompt; images are sent to vision models. **Web search** has a model turn the question into search queries, searches the web (Brave, Bing, then SearXNG), and the answer cites the sources as `[1]`, `[2]` …. **Instructions** add a per-chat system prompt.
- **Tools.** With the **Chat tools & code** permission, the model can use the MCP servers you tick under **Tools** and a **code interpreter** that runs Python, Node or shell. Tools marked "requires approval" (e.g. the shell's `run_command`) are offered only to administrators who tick *Allow tools that need approval*.
- **Reasoning effort** (in the message box):
  - **None**, **Low**, **Medium** (the default) and **High** set the model's `reasoning_effort`. Models that do not support it are remembered and are then asked without it.
  - **Ultracode** asks for the most careful, complete engineering work.
- **Permission mode** (in the message box): every tool call goes through it.
  - **Manual**: Agentic asks before it uses a tool for the first time in the chat, or before it opens a new website.
  - **Auto** (the default, `Chat.DefaultPermissionMode`): Agentic runs on its own and never stops to ask. The exception is a desktop app MCP server set to *Ask first*, which asks in both modes.
  - An approval offers **Allow once**, **Always allow in this chat** and **Deny**. Any of your open windows can answer it: the web app, another tab, or the desktop app.
- **Context:** the bar in the message box shows how much of the model's context window the chat uses (what you are typing is included). While an answer is written it moves with every request the model gets (the provider's count when it reports one) and with what the model writes; between messages it shows what the next message sends. **Compact** summarizes the older messages: the model then gets the summary plus the newer messages, and the summarized messages are dimmed with a divider.
  - Compaction also happens automatically when the chat passes `Chat.AutoCompactPercent` (80%) of the window, or `HistoryMessages`.
  - Window sizes are known for common model families. `Chat.ContextWindows` overrides them, for example `{"my-model": 32000}`.
  - Within one answer, each model call must fit the window with room to answer, because providers refuse longer prompts and never truncate them. Long tool loops (the CLI and the desktop app run many) shorten earlier tool output, oldest first, and the cut says how much was left out. If the provider still reports that the prompt is too long, Agentic cuts harder and sends that step once more.
- **The local model's slots.** Chats (and API calls) with the local model take one of its slots (`LocalModels.Slots`), shared with the agents: **Models & access** counts them ("1 in chat").
  - People go before normal-priority agent work.
  - When every slot is busy, the answer shows its place in the queue ("In the queue for my-local-model: all 4 slots are busy · you are next") until the model starts thinking.
- **All your windows stay in step.** An answer that is being written in another window, or in the desktop app, shows live in every window that has the chat open, together with its approvals and questions. The chat list marks chats that are being answered and chats that **need you**.
- **Streaming.** Answers stream token by token. Reasoning appears in a collapsible "thinking" block and tool calls appear as cards with input and output. **Stop** (or Esc) cancels the answer: what it wrote so far stays and is saved (a message sent right after waits for that). Messages can be copied or regenerated.
- **Typing while it answers.** A message sent while the answer runs goes to the model before its next step (**Next step** above the message box); one the answer can no longer take is sent as the next message when it ends (**Queued**). Both show above the message box with **Edit** (back into the box) and **×** (remove) until they are read. After **Stop**, messages that were not read go back into the box.
- **Try again after a failure.** When the model's connection fails past its retries, the answer ends with the error and **Try again**. On an answer that had already written something or run tools, **Try again** (and `/retry`) picks up where it stopped: its text, tool calls and their results stay in the answer and are sent back to the model, which carries on without redoing them. **Start over** answers the question again from the beginning (the failed answer is kept as a version). An answer that already has work in it (tool calls, a task list with open steps, or a failed answer it continues) is retried `Chat.RetriesWithWork` (12) times instead of `Chat.Retries` (5). If it still fails, the open chat page tries it again by itself after 1, 3 and 10 minutes, from that window so the tools on your computer are there; sending a message cancels the wait. Typing "keep going" (or "continue", "try again", "resume" …) right after a failed answer carries on with that same answer, and any other message is sent with a note that the previous answer was cut off, with its task list, so the model carries on with the plan instead of starting over.
- **Long chats stay fast.** Only the latest 40 messages are drawn; scrolling up to the top (or **Show earlier messages**) draws the next 40 without moving what is on screen, and a long session trims the page back to the latest messages while you are at the bottom. Messages off screen are not laid out or painted (`content-visibility`). The model's history is not affected, only what the page draws (chat tab, desktop app and VS Code alike).
- **Rich answers.** Answers render Markdown: tables, task lists, callouts (`> [!TIP]`, `[!WARNING]` …), `==highlights==`, coloured text (`<span style="color:#16a34a">…</span>`), citations, and highlighted code.
- **Running code.**
  - HTML, SVG and JavaScript blocks have **Preview / Run in browser**. The code runs in a sandboxed frame (`/sandbox/frame`, opaque origin, its own CSP, no access to the console's cookies or API), and `console.log` output and errors are shown under it.
  - Python, Node and shell blocks have **Run**. They execute in the code sandbox MCP server (`Chat.CodeServerId`, default `code-exec`, which needs `write_file` and `run_command`). The output and exit code are shown under the block.
  - Code never runs on the host itself unless `Chat.AllowLocalCodeExecution` is on, and then only for administrators.
- **Downloading what you make in the sandbox.** Files created in the sandbox (by Run, or by the model/you in the terminal) can be pulled back out:
  - The model has a **`share_file`** tool: ask it to "download/save/export" a file and it attaches the file to its answer with a **Download** button (set `as_zip` to bundle a folder, or several files, as one `.zip`).
  - The **Files** button on the composer opens a browser for the sandbox: navigate folders, download any file, or download a whole folder as a `.zip`. There is no binary download tool in Open Terminal, so files come back base64-encoded over MCP; `Chat.Sandbox.MaxDownloadMb` (default 100) caps a single download. Turn the whole feature off with `Chat.Sandbox.AllowDownloads`.
- **Sandbox resources are cleaned up.** Long-lived things the host started in the sandbox — background processes and Docker containers — are stopped once they go idle or reach a maximum age, so the sandbox host is not left running them forever (`Chat.Sandbox`, the **Sandbox** admin page). This is deliberately conservative: only resources the host itself started are ever stopped. Every container that already existed when the host started is recorded **protected**, Docker **Compose** stacks are treated as infrastructure and left alone (unless a project is listed in `ManagedComposeProjects`), and `NeverTouch` is a hard keep-list. Lifetimes: `ProcessIdleMinutes` / `ProcessMaxLifeMinutes` and `ContainerMaxLifeMinutes` (0 disables a limit). Container management needs passwordless `sudo docker` in the sandbox; without it (`ManageContainers:false`, or no sudo) only processes are reaped. When a chat's resource is stopped, that chat gets a note (`NotifyOwner`). Admins see everything running and can **Stop** any of it on the **Sandbox** page (`GET /api/sandbox/resources`, `POST /api/sandbox/resources/{id}/stop`).
- **Open the apps it builds (app previews).** A web app the assistant runs in a sandbox container (Docker, with a published port) opens in your browser at **`/container/{id}/`** on Agentic's own port — a reverse proxy, no other port to open. The model gets a **`preview_app`** tool (and is told to build such apps in Docker with a published port); its answer then shows an **Open app** card. The **Files** button lists **Running apps**, and so does the **Sandbox** page. API: `GET /api/sandbox/apps`, `POST /api/sandbox/apps/preview {container, port?}`, `DELETE /api/sandbox/previews/{id}`.
  - **Only the assistant's own apps**: the same rule as the reaper — never a container that predates the host, a Compose stack (unless managed) or anything on the keep list, so infrastructure on the sandbox host (OpenWebUI, databases…) can never be reached this way. Each request re-checks that the container still runs and still publishes the port.
  - **The link is the key**: `/container/{name}-{random}/` (110 random bits), valid `PreviewHours` (24), closed when the container is stopped or with `DELETE`. Anyone with the link can open the app, so share it like a password.
  - **Isolated by default** (`PreviewIsolation: sandbox`): every preview response carries `Content-Security-Policy: sandbox`, so the page runs as an opaque origin and its scripts cannot use your Agentic session (the session cookie is `SameSite=Strict` and never sent from it). The host's session cookie, API key (`Authorization`) and CSRF token are never passed to the app, and `Referrer-Policy: no-referrer` keeps the link from leaking. Apps that need their own cookies or a login may not work isolated; `PreviewIsolation: none` runs them as the Agentic site itself — only for apps you trust, since their scripts could then act as you.
  - **Paths**: the app is served under the prefix, so root-absolute URLs are rewritten (HTML attributes, CSS `url()`, redirects), and a small script keeps runtime ones (`fetch('/api')`, XHR, WebSocket, history) under it; `localStorage` works in memory when the isolated page cannot have its own.
  - **How it reaches the app** (`PreviewTransport: auto`): straight to `PreviewHost:port` (default: the code server's host) when Agentic can reach it — full speed and WebSockets. Docker in **WSL2 on Windows** publishes ports only inside its VM, so there each request goes **through the sandbox connection** instead (curl inside the sandbox to its Docker host): no setup needed, pages, forms and APIs work (uploads up to 48 KB), but not live WebSocket features (Streamlit, Gradio, hot reload). For those, forward a block of ports on the Windows host (`netsh interface portproxy` to the WSL VM, plus a firewall rule allowing only this host) and set **`PreviewPorts`** to it (e.g. `"20000-20499"`): the model is then told to publish apps on a free port in that range (each message names a few that are free right now), and their links go direct.

#### Versions, projects, memory and sharing

These work like their ChatGPT and Claude counterparts. The desktop app has them too, since it shows the same page. The CLI uses the same server, so memory, personal instructions and project instructions apply there as well.

#### Chat and agents

- **Parallel agents.** When a chat has tools, the model works as an orchestrator: it is told to split independent work (exploring several areas, researching several questions, reviewing several files, separate changes) into `task` subagents started in one response, and those run **at the same time** (`Chat.MaxParallelSubagents`, default 6; they queue fairly for the local model's slots). `subagent_type: explore` gives a read-only subagent (search, read, web), safe to run many at once. A round's read-only calls (reading or searching several files on your computer, read-only MCP tools) also run together (`Chat.MaxParallelTools`, 8), and so do a subagent's own reads; anything that changes something, needs an approval or repeats a call still runs alone and in order. Each subagent's card shows its name, type and live progress. Per chat: **Tool limits** (`/limits`) has a **Parallel agents** switch and **Subagents at once**; `/parallel on|off|N` sets them. The CLI's own `Task` subagents run in parallel the same way (`/parallel` there too). `Chat.SubagentMaxRounds` (50) is how often a subagent's progress is checked: one still getting things done goes on for another stretch; one whose stretch brought nothing new is told 3 steps ahead to finish, and its last step (without tools) is its report (`Chat.SubagentMaxTotalRounds`, 0 = none, is a fixed total). Every report gives the subagent's id; one that ran out of steps, or stopped because its model failed (it still reports what it did), is continued with `resume` and that id, keeping everything it already read, instead of started over (kept 2 hours per chat).
- **Subagents that keep going.** A subagent's model call that fails is asked again like the answer's own: an outage, a 429 or 5xx, a stream that breaks off mid-answer (no `[DONE]`), or no answer in time is retried up to `Chat.Retries` times (`GoalRetries` with a goal), `RetrySeconds` apart, and its card says so; a refused call (a bad request) is not retried. The provider's own error text stays in the log.
- **Forks and nested subagents.** `fork: true` starts a subagent with the whole conversation so far (for side tasks that need its context) instead of only its prompt. Subagents can start subagents of their own, up to `Chat.MaxSubagentDepth` levels (3; an explore subagent only starts explore ones); they show nested in their parent's card. The CLI's `Task` does both too.
- **Scheduled prompts.** `/schedule every 30m <prompt>` (or `hourly`, `daily 09:00`, `weekdays 08:30`, `at 14:00`, `once 2026-11-01 09:00`) has the server send a prompt to the chat on a schedule, also while no window is open; times are in your time zone. `/schedule` lists, pauses, runs and deletes them. Each run is the user's own answer, with their access and limits as they are then; a chat that is busy waits a minute. API: `GET|POST /api/chat/schedules`, `PATCH|DELETE /api/chat/schedules/{id}`, `POST /api/chat/schedules/{id}/run`; `Chat.SchedulePollSeconds` (30).
- **Monitors.** In the desktop app and VS Code the model can watch a command (`monitor`: a log, a dev server, a watch build): its output lines, those matching a filter, go into the chat as they happen, and when the chat is not busy the server answers them by itself, at most every 15 seconds. They show folded as **Monitor events**. API: `POST /api/chat/conversations/{id}/monitor`. The CLI has the same (`Monitor`).
- **Run and test the project.** In the desktop app and VS Code, the **Run** chip in the message box (or `/run`) lists the ways to run the project on that computer — its `.agentic/launch.json` or Claude Code's `.claude/launch.json`, else what its files show (package.json scripts, a static site, Python, .NET, Go, Rust, a Makefile) — and runs them there, like Claude Desktop's preview: a web app opens next to the chat once it has an address, and tests and apps show their output as it comes with **Restart** and **Ask Agentic to fix it**. **Fix how it runs** has the model write `.agentic/launch.json`. The page (`chatrun.js`) uses the app's `window.chatClient.run`; the model gets the app's `preview` tool. `/run <name>` starts one, `/run stop [name]` stops, `/run share [project]` shares its website. The CLI has the same `/run` (stop, status, log, open, share, edit).
- **Artifacts** (`/artifacts`, in the menu under Chat; `/artifacts` in a chat, or **Artifacts in this chat** in its ⋯ menu): everything you made that runs. The pages and drawings (HTML and SVG code blocks) from your chat answers run there in the sandboxed preview; in the desktop app and VS Code the projects of the workspace folders are there too, with their ways to run. **Share** gives a page, or a project's website (its build — `dist`, `build`, `out`, `_site`, `public`, `www` — or a static site's folder, zipped by the app without hidden files and `node_modules`), a public link `/a/{token}` anyone can open without signing in; sharing a project again replaces its files under the same link. The link runs as an isolated origin (`Content-Security-Policy: sandbox`, served only framed by this server or opened on its own), so it cannot use the viewer's session; **Stop sharing** ends it. Making links needs the "Share chats" permission; `Chat.ArtifactSharing` (on) turns the feature off — existing links then stop opening — `Chat.MaxSharedArtifacts` (50 per user) and `Chat.MaxSharedSiteMb` (25) limit it. API: `GET /api/chat/artifacts[?chat=]`, `GET /api/chat/artifacts/code?chat&message&block`, `POST /api/chat/artifacts/share` {chatId, messageId, block}, `POST /api/chat/artifacts/share-site?title&source&key` (body: a zip with index.html), `DELETE /api/chat/artifacts/shared/{token}`, `GET /api/public/artifacts/{token}` (no sign-in). Migration 0027 adds `SharedArtifacts`; websites are kept in `<data>/shared-artifacts/{token}/`.
- **More commands.** `/btw` (a side question answered aside, not added to the chat), `/recap`, `/research` (several searches, sources read and cross-checked, a cited report), `/fork`, `/loop`, `/security-review`, `/simplify`, `/verify` and `/notify` (system notifications when an answer finishes or the chat needs you). Also `/context` (how full the context window is, with **Compact now**), `/status`, `/remember <fact>`, `/forget <words>`, `/read` (the last answer aloud), `/mcp` (the Tools menu), `/skills` (your skills, commands and saved prompts) and `/release-notes`.
- **Mermaid diagrams.** A finished ` ```mermaid ` block in an answer is drawn as a picture (**Code** shows the source). The server draws it with the same mermaid-cli and Chrome as the data server's `render_mermaid`; the paths come from the `McpServers` entry started with `--mermaid-cli`, so nothing more is configured (no entry: the code stays code). Drawings are kept in memory (300) and at most two are drawn at a time. API: `POST /api/chat/mermaid` {code, theme: default | dark} → `image/svg+xml`, 400 with the parse error; `/api/chat/meta` says `mermaid`.
- **Stable Diffusion and Suno in chat.** The **SD** and **Music** chips (`/sd`, `/music`) give the model `stable_diffusion` (pictures on the Images tab's server, waiting their turn in the same line; they also go to the gallery; a picture from the chat can be changed with `reference_image` and `strength`) and `make_song` (two songs with Suno, also kept in the Music library; shown with a player and a download button). They need the tab's permission (`images.sd`, `music.suno`) and are paid for as in the tab; `make_song` uses Suno credits, so the model is told to call it only when you asked for a song (in Manual mode it asks first).
- **Archive and import.** **⋯ → Archive** (`/archive`) takes a chat out of the list without deleting it; `/archived` lists archived chats to open or bring back. **⋯ → Import chats from a file** (`/import`) reads a chat exported here as JSON, or ChatGPT's or Claude.ai's `conversations.json` (one conversation or the whole list, at most 50 chats of 2,000 messages); only the text of the questions and answers comes along. API: `POST /api/chat/import?model=` (body: the file's JSON) → `{chats: [{id, title, messages}]}`.
- **Heartbeat.** `/heartbeat on [30m]` opens a checklist that the server sends to the chat on that schedule (like the CLI's, also while no window is open; it is a scheduled prompt that starts with "Heartbeat check."). The model acts on what needs attention; when nothing does, it answers `HEARTBEAT_OK` and the check and its answer leave the chat again (the schedule's last status says "nothing needed attention"). `/heartbeat edit`, `now` and `off`.
- **The desktop app and VS Code.** `/bashes` (or `/tasks`) and `/monitors` list the commands the model left running in the background and the commands it watches, with their last output and **Stop**. `/hooks` lists the hooks; besides `PreToolUse`, `PostToolUse` and `Stop`, `UserPromptSubmit` runs before a message is sent (exit code 2 stops it; what it prints goes with the message) and `SessionStart` the first time a chat is used in that window. `/agents` lists your own subagent types — Markdown files with front matter (name, description, tools) in `.claude/agents` or `.agentic/agents`, the project's or your home folder's — and creates new ones; the app sends them with each message (`clientAgents`), the `task` tool offers them as `subagent_type`, and such a subagent works by its file's instructions with only the tools it lists (Claude Code's names like `Read`, `Grep`, `Bash` or `mcp__server__tool` map to the tools here). `/worktree [name | list | done | remove <name>]` works in a git worktree (`.agentic/worktrees/<name>` on the branch `agentic/<name>`, like the CLI) and `/diff` shows the working tree's changes. VS Code now also has **Claude Code** as a model, like the desktop app.
- **Hand a chat to agents.** `/delegate` (or **⋯ → Hand this chat to agents**) turns the conversation into a task for one agent or a global task for all of them. With **Post the result back into this chat** (on by default) you stay in the chat: a note says who took the work, and when it is done the result arrives as a message from the agent (after its review, when it has one; a global task posts its final answer). The model sees these reports in the history, so you can ask about them straight away. API: `chatId` in the body of `POST /api/agents/{id}/task` or `POST /api/global-tasks`.
- **Discuss in chat.** The task details dialog, a global task and a result have **Discuss in chat**: it opens a new chat whose first message is the work (what was asked and what came back). API: `POST /api/chat/discuss` with `{agentId, taskId}`, `{globalTaskId}` or `{resultId}`.
- Links in these reports (`/agents/X?task=Y`, `/global-tasks/…`) open the page in the app; `?task=` opens that task's details.

- **Edit a question.** The pencil under your message edits it and asks again. Press ↑ in an empty message box to edit your last message.
- **Versions.** Editing a question or regenerating an answer keeps what it replaced as an earlier version. **‹ 2 / 3 ›** switches between versions; versions inside an earlier version come back with it. The model only ever sees the version shown. Stored in `ChatBranches`.
- **Continue.** An answer that stopped early can be continued in place: one that hit the length limit, ran out of tool rounds, was stopped, or failed part way. The new text joins the same message. A new list item or line of code starts on its own line.
- **Feedback.** 👍 / 👎 under an answer. A 👎 can carry a note. Both are stored with the message (`Feedback`, `FeedbackNote`).
- **Knowledge (RAG).** The **Knowledge** button lets a chat answer from your own documents (Retrieval-Augmented Generation). Build a named *knowledge base* from uploaded files (text, code, PDFs, spreadsheets…): each file's text is split into overlapping chunks and embedded. When a base is selected, your question is embedded and the chunks most like it (cosine similarity, top `Rag.TopK`) are given to the model as cited `[n]` context, so large corpora are read without stuffing everything into the prompt. The citations show as sources under the answer, linking to the file; they are **private** — stripped from read-only share links.
  - Manage bases from the Knowledge button → *Manage knowledge bases*: create a base, add files (indexed on upload, with a status), remove files or the base.
  - Needs an **embedding model** on the provider. Set `Rag.EmbeddingModel` to its id (e.g. `bge-m3`, `text-embedding-3-small`), or leave it empty to auto-detect a model whose id looks like an embedding model. Without one the Knowledge button is hidden.
  - Bases are per-account; the API is `/api/chat/knowledge*` (needs *Use models*). Config: the `Rag` section (chunk size, overlap, top-K, minimum score, context limit).
- **Read aloud** speaks the answer in your voice through the XTTS speech server (`TextToSpeech`, see **Speech and the Audio tab** below), streamed so it starts after about 1.5 s; without a speech server it uses the browser's speech synthesis. Code and formulas are skipped.
- **Voices.** `/voice` opens the voice picker (the server's built-in speakers and cloned voices, each with a preview); `/voice damien` picks by name. The voice is per account (`AudioPrefs`).
- **Dictation mode** (headset button, or `/dictation`) is a spoken conversation. It listens; when you pause (`silenceMs`, 1.4 s) what you said is sent; when the answer is done a model writes a short spoken summary of it (a **TL;DR**: about `summaryWords` words, no Markdown or code) in the voice's personality. The summary is shown on the answer and spoken, then it listens again. **Skip** (or Esc) stops the speech, **Send** sends before the pause, **Pause** and **Talk** stop and resume listening, and saying only "stop", "that's enough for now", "thanks, that's all", "goodbye" and the like ends it without sending anything ("stop the server" is still a message). When the model asks a question (ask_user), Dictation mode reads the question and its options instead of a summary, and what you say answers it ("two" or an option's name picks that option); clicking an answer or typing a message stops the listening, and that answer is spoken as usual.
  - Listening uses the browser's speech recognition (Chrome, Edge). In the desktop app and Firefox the microphone is recorded until you pause (loudness against the room's noise, `sensitivity`) and turned into text by the speech-to-text below.
  - The summary is kept with the message (`ChatMessages.SpokenSummary`, `SpokenVoice`) and shown when the chat is opened again. `/tldr` writes and speaks one for the last answer outside Dictation mode.
  - Each voice has a **personality** (Audio tab → Personalities): the prompt the summary is written with ("You are a drill sergeant…"), the model that writes it (empty: `TextToSpeech.SummaryModel`, else the answer's own model, then `Chat.TaskModel`, then `Chat.DefaultModel`), its language, and its speech settings. Some models tone down a rough personality: give such a voice a model that plays along.
- **Dictation** (microphone button) uses the browser's speech recognition where it works. In the desktop app, Firefox, or when that service is unreachable, the recording goes to the provider's speech-to-text instead: `POST /api/chat/transcribe`, which forwards it to OpenWebUI's `/api/v1/audio/transcriptions`, so OpenWebUI must have speech-to-text set up.
- **Images.** The **Image** toggle gives the model a `generate_image` tool. Images appear in the answer and can be downloaded. User accounts pay the *image-generation* price from Finance → Pricing.
  - Any chat model can use the tool, because the picture is never made by the chat model itself.
  - Pictures come **only** from the images API in `ImageGeneration` (`BaseUrl` + `ApiKey`): `POST {BaseUrl}/images/generations`. OpenWebUI and its image models are never used for pictures. Chats, agents and `/v1/images/generations` all use this path. Without `BaseUrl` and a key there is no image generation, and the Image toggle is hidden.
  - `ImageGeneration.Model` (`gpt-image-2`) is asked first. When it cannot make the picture, `FallbackModels` are asked in order for that request (`gpt-image-2.5-flare`, `gpt-image-2.5-sunburst`). `AutoFallback: true` would also try the API's other image models; it is off, so only the listed models are used.
  - A model that failed is skipped for `FailedModelPauseSeconds` (180), unless nothing else is left. A minute after every model failed, a new request gets that answer at once instead of asking them all again.
  - The answer names the model that made the picture.
  - The chat provider's image models (`gpt-image`, `dall-e`, `flux` …) are not offered as chat models, and `/v1/chat/completions` refuses them.
- **Changing a picture.** With Image on, attach a picture and say what to change ("make the sky purple"). The model calls `generate_image` with `reference_image`: the file name of a picture in the chat, attached or made earlier, or `latest`. The picture and the prompt go to `POST {BaseUrl}/images/edits` (multipart, one picture; PNG, JPEG or WebP up to 20 MB), with the same models and fallbacks as new pictures. Without a size the result keeps the picture's shape. The model is told which pictures the chat has, and the result names its source ("from photo.png"). It is priced like a new picture.
- **Attachments.** A message can only carry the sender's own uploads (administrators: any).
- **Image or Video, one at a time.** A chat has Image or Video on, never both: switching one on switches the other off (with a short note). The server enforces it too. A chat saved with both on keeps Image.
- **Image sizes.** Image APIs often take their documented sizes only (others answer 400), so `AllowedSizes` lists the 1K ones: 1024x1024, 1280x720, 720x1280, 1152x864, 864x1152, 1248x832, 832x1248. The model chooses from them.
- **Videos.** The **Video** toggle, next to Image, gives the model a `generate_video` tool (`prompt`, `seconds` up to `MaxSeconds`, `ratio` 16:9, 9:16, 1:1, 4:3, 3:4 or 21:9). It is shown on phones too: in a narrow message box it becomes a camera icon.
  - Videos come **only** from the video API in `VideoGeneration` (for example Seedance): `POST {BaseUrl}/video/generations` starts a task and `GET {BaseUrl}/video/generations/{id}` is checked every `PollSeconds` until the video is ready. A 5-second 720p clip takes about 2–4 minutes; the answer shows the progress meanwhile.
  - `seedance-2.0-fast` is asked first, then `seedance-2.0-mini` when it cannot make the video.
  - The video is saved with the chat (up to `MaxVideoMb`) and plays in the answer, inline on phones, with a Download link. It is served with byte ranges, which iOS Safari needs to play it.
  - User accounts pay `PricePerSecond` (or `ModelPricePerSecond` for that model) per second of video; nothing when it is null.
  - Video links outside the video API are downloaded only over https, from public addresses, without its key, and must return a video.
- **Pictures for a model that cannot read them.** A message with pictures sent to a model that cannot read images (the local Qwen) is answered by a vision model, for that message only. The answer shows a *Vision* route badge.
  - The vision model is the first that your account may use: `Vision.ModelKeys` first, then the other models configured with the `Vision` capability.
  - Models configured without that capability are known not to read images. Any other model that answers with an error such as "started without the vision encoder" is remembered as one.
  - When your account may use no vision model, the pictures are sent as their descriptions instead.
- **Math.** LaTeX renders as formulas (KaTeX, MathML output): `$…$` and `\(…\)` inline, `$$…$$` and `\[…\]` on their own line. A `$5` amount stays text.
- **Projects** (left column):
  - A project groups chats under shared **instructions** and **knowledge files**. The files' extracted text, about 60,000 characters in all, goes with every message of the project's chats.
  - Click a project to see its chats; a new chat then starts in it. The ⋯ menu moves a chat into or out of a project.
  - Deleting a project keeps its chats.
- **Personalization** (bottom of the left column, or Ctrl+Shift+I):
  - **What the assistant should know about you** and **how it should respond** apply to all of your chats.
  - **Memory:** with memory on, the model gets the `remember` / `forget` tools and sees the list of what it remembers. A saved memory shows as "Memory updated" in the answer.
  - You can add, delete or clear memories yourself. Anything that looks like a key or password is refused.
- **Temporary chats** (the *Temporary* toggle on a new chat):
  - They never appear in the history, and memory is neither read nor written. Personal instructions still apply.
  - They are deleted a day after their last message, and they cannot be shared.
- **Share** (link icon in the header): creates a read-only public link `/share/{token}`. Anyone with the link can read the conversation, including later messages, without signing in. Reasoning, tool output and costs are left out. **Stop sharing** makes the link stop working.
- **Export** (⋯ menu): Markdown or JSON.
- **Saved prompts:** type `/` at the start of the message box to pick one, or manage them with the prompt icon in the left column. In the desktop app they appear with the skills, and in the CLI they work as `/commands`.
- **Shortcuts:** every one can be changed in **Keyboard shortcuts**: the user menu, the command palette, the keyboard icon under the
  chat list, `/shortcuts`, or Ctrl+/. Click a shortcut and press the new keys (or **+** for another one); two keys pressed one after
  the other, like Esc Esc, also work. A key already used in the same place is offered to move. Changes are saved to the account
  (`GET`/`PUT /api/account/keybindings`, only the changed ones) and apply in every browser, the desktop app and VS Code. Defaults:

  | Keys | Action |
  |---|---|
  | Ctrl+Shift+O | New chat |
  | Shift+Esc | Focus the message box |
  | Ctrl+Shift+C | Copy the last answer |
  | Ctrl+Shift+; | Copy the last code block |
  | Ctrl+Shift+S | Show or hide the chat list |
  | Ctrl+Shift+⌫ | Delete the chat |
  | Ctrl+K | Command palette |
  | Enter / Shift+Enter | Send / new line |
  | Esc · Esc Esc | Stop the answer · go back to an earlier message (empty box) |
  | ↑ | Edit your last message (empty box) |
  | Alt+Shift+M · Alt+1…4 | Multitask on or off · go to a pane |
  | 1 … 5 | Pick an answer when the model asks you |

Endpoints (all under `/api/chat/`):

| Endpoint | Purpose |
|---|---|
| `conversations/{id}/versions` | Switch to an earlier version |
| `…/share` | `POST` creates the link, `DELETE` removes it |
| `…/export?format=md\|json` | Export the chat |
| `…/messages/{id}/feedback` | Rate an answer |
| `projects` | Create and manage projects |
| `personalization` | Personal instructions and the memory setting |
| `memories` | Add, delete and clear memories |
| `prompts` | Saved prompts |
| `files/{uploadId}` | The account's own images |
| `transcribe` | Speech to text |

Message sending also accepts `editMessageId` and `continue`. The public page reads `/api/share/{token}` and `/api/share/{token}/files/{uploadId}` without signing in.

### Speech and the Audio tab

Speech comes from a self-hosted **XTTS-v2** server (`TextToSpeech`): 58 built-in speakers, cloned voices, 17 languages, 24 kHz mono 16-bit audio. Chat reads answers aloud and talks in Dictation mode with it (above); the **Audio** tab (under Workspace) uses all of it. Everything needs the **Voice** permission (`chat.voice`).

- **Setup.** `TextToSpeech.BaseUrl` is the server (e.g. `http://192.168.1.20:8020`). Its `X-API-Key` never reaches a browser: an administrator sets it on Audio → Settings, which keeps it in the vault as `TextToSpeech.ApiKeySecret` (`xtts-api-key`), bound to the server's host and usable by no agent. `TextToSpeech.ApiKey` (or `AGENTIC_TextToSpeech__ApiKey`) wins over the vault.
- **One line.** The server makes `MaxConcurrency` (1) request at a time. Every other request waits in one line here, first come first served, until the one being made ends. That covers speech, streams and clones from the Audio tab, Read aloud, Dictation mode and the API. The wait has its own limit (`TimeoutSeconds`; then 503), and the work's time starts when its turn comes. While a request waits, the page says where it is ("Waiting for the speech server: next in line"). `GET /api/audio/queue` shows the line: slots, running, waiting, and your own requests with their positions.
- **Speak.** Any text (up to `MaxTextChars`, 20,000), any voice and language, and every setting: `speed` (0–3), `temperature` (0–2), `top_k` (1–1000), `top_p` (0–1), `repetition_penalty` (1–50), `length_penalty`, `enable_text_splitting`, and for streams `stream_chunk_size` (5–200). Each is checked here and refused with its name when out of range. **Stream** plays while it is made (`/tts/stream`, PCM or WAV, first sound after about 1.5 s); **Whole file** uses `/tts`. When a stream brings no audio at all (the server's stream failed right after it started), the whole file is made instead. Presets: Default, Steady (temperature 0.6, repetition penalty 7), Expressive, Slower, Faster. *Use the voice's own settings* speaks with the personality's settings, which Chat uses.
- **Clips.** Kept speech (`AudioClips`, `data\audio\clips`, the newest `MaxClipsPerUser` (200) per account) plays again, downloads as WAV or raw PCM, and its text and settings can be used again.
- **Voices.** Clone a voice from clips (files, or a recording made on the page, sent as WAV): 6–30 s each of one speaker, 2–4 varied clips clone best. Every cloning setting is there: `trim_silence_db`, `normalize`, `gpt_cond_len`, `gpt_cond_chunk_len` (at most `gpt_cond_len`), `max_ref_length`. Cloning needs a confirmation that the voice is your own or its speaker agreed; a name in use is replaced. The server deletes a cloned voice 30 days after its upload, so Agentic keeps its clips (`AudioVoiceSources`, `data\audio\voices`) and **Renew** uploads them again. Details show what the server has (clips, settings, expiry). Only the account that cloned a voice (or an administrator) can renew or delete it.
- **Personalities.** Per account and voice (`AudioVoiceProfiles`): the TL;DR prompt, its model, language and speech settings, tried out on a sample answer before saving.
- **Dictation.** Each account's Dictation mode settings: pause that ends a turn, summary length, longest turn, when to stop listening, microphone sensitivity, listen again after speaking, say when an answer fails.
- **Settings** (administrators only; other accounts do not see the tab). Speech pricing, the server and its key.
- **Pricing.** Speech is paid from the account balance like tokens in and out: **text in** (USD per 1M characters sent), **audio out** (USD per minute of speech made), and a **voice upload** price per clone or renewal. Administrators set them on Audio → Settings, and administrators and *Manage money* on Finance (`PUT /api/audio/pricing`, kept with their history in `AudioPricing`; `TextToSpeech.PricePerMillionChars` / `PricePerMinute` / `PricePerVoiceUpload` are the starting prices, none = free). Read aloud, Dictation mode and the Audio tab all charge this way, with the account's surcharge, as `Speech` ledger entries (model `xtts`); a stream is charged when it ends, for the audio that reached the page. A User account whose balance does not cover the estimate (the text, and about 14 characters a second of audio) is refused with 402 before anything is made. The spoken summary is a model call, charged as a chat answer at that model's Model Square token prices. Administrators are never charged; the Audio tab shows users what speech will cost them, and each kept clip what it cost.

| Endpoint (`/api/audio…`) | |
|---|---|
| `GET` · `GET queue` | Status, voices, languages, defaults, your voice, personalities, cloned voices' sources, the line · the line alone |
| `PUT prefs` · `PUT profiles` · `DELETE profiles?voice=` | Your voice and Dictation mode settings · a voice's personality |
| `POST tts` · `POST stream?format=pcm\|wav` | Speech: the whole WAV (`X-Clip-Id` when `save`) · as it is made |
| `POST summary` | The spoken summary of `{chatId, messageId}` (kept with it) or of a `text` |
| `GET clips` · `GET clips/{id}/audio?format=pcm&download=1` · `DELETE clips[/{id}]` | Kept clips |
| `POST voice-clips?name=` · `POST voices` · `GET voices/{name}` · `POST voices/{name}/renew` · `DELETE voices/{name}` | Cloning |
| `PUT key` | The speech server's key (administrators) |
| `GET pricing` · `PUT pricing` | Speech prices: `perMillionChars`, `perMinute`, `perVoiceUpload` (setting them needs *Manage money*) |

### Pictures and the Images tab

The **Images** tab (under Workspace) makes pictures with a self-hosted **Stable Diffusion 1.5** server (`StableDiffusion`; FastAPI with `/jobs`, `/preprocess`, `/options`, `/health`). Everything needs the **Images (Stable Diffusion)** permission (`images.sd`).

- **Setup.** `StableDiffusion.BaseUrl` is the server (e.g. `http://192.168.1.30:8000`). Its key (`api_key.txt` on that machine) never reaches a browser: an administrator sets it on Images → Settings, which keeps it in the vault as `StableDiffusion.ApiKeySecret` (`sd-api-key`), bound to the server's host and usable by no agent. `StableDiffusion.ApiKey` (or `AGENTIC_StableDiffusion__ApiKey`) wins over the vault.
- **Create.** Every field of the server's `GenerateRequest`, each checked against its range before it is sent: `prompt` and `negative_prompt` (A1111 syntax; the server's embeddings are one click away), `model`, `vae`, `scheduler` (the server's lists, or any file stem or Hugging Face repo id), `width`/`height` (64–2048, rounded down to a multiple of 8; with an init image they can follow its size), `steps` (1–150), `guidance_scale` (0–30), `guidance_rescale`, `eta`, `clip_skip` (1–12), `seed` (−1 is random; picture *i* uses seed + *i*), `num_images` (1–16), `freeu`, `loras` (name and weight −4 to 4), **img2img** (`init_image`, `strength`), **inpainting** (`mask_image` painted on the page over the picture, or uploaded; `invert_mask`, `mask_blur`, `inpaint_padding` for only the masked area), **ControlNet** (several at once: `model`, `image` (empty: the init image), `preprocessor`, `preprocessor_params`, `scale`, `start`, `end`; a *Preview map* runs the preprocessor; `guess_mode`) and the **hires fix** (`scale`, `upscaler`, `steps`, `denoise`; text to image only). Pictures come from files, drag and drop, or the gallery (sent as `gallery:{id}`, which the server turns into the picture).
- **Jobs.** The server makes `MaxConcurrency` (1) job at a time.
  - **The line.** Every job waits in one line here, first come first served, from every account and the API. It is sent (`POST /jobs`) only when the job before it ends. A waiting job has `inLine: true` and `queuePosition` (1 = next). The page says "Waiting in line: next" and can take it out of the line; it then never reaches the server. Preprocessor runs wait in the same line. Its full request (pictures included) is kept in `data\images\queue` until it is sent, so a job still waiting when the host stops is sent after the restart.
  - **Following a job.** A sent job is followed here (`GET /jobs/{id}` every `PollMilliseconds`), not by the page. The page shows the progress (step, picture *n* of *m*) and the live preview, and can cancel. The pictures reach the gallery even when the page was closed. Sent jobs that had not ended are followed again after a restart (`SdJobs`; `RemoteId` is the server's id).
  - **Limits.** An account may have `MaxActiveJobsPerUser` (3) jobs waiting or running. A job is cancelled `JobTimeoutSeconds` after it was sent; time in line does not count.
- **Gallery.** Every picture is kept (`SdImages`, `data\images`, the newest `MaxImagesPerUser` (500) per account; starred ones always) with its settings, seed and the PNG's A1111 `parameters` text (PNG info). The viewer downloads it, reuses its settings or seed, and sends it to img2img, inpainting (the mask painter opens) or a ControlNet.
- **Tools.** Any preprocessor on any picture (with its params), the map downloadable or sent to a ControlNet; the server's health (GPU, model, queue, VRAM) and everything `/options` lists, with the folders new files go in (SD 1.5-family files only).
- **Pricing.** Per picture made and per minute the GPU worked on the job (the server's elapsed time), set by administrators on Images → Settings, and by administrators and *Manage money* on Finance (`SdPricing`; `StableDiffusion.PricePerImage` / `PricePerGpuMinute` are the starting prices). Charged when a job is done, as `Image` ledger entries with the model's name and the account's surcharge; failed and cancelled jobs are not charged. A User account whose balance does not cover the estimate (about 0.4 s per 512×512 step on a GTX 1660 Ti) is refused with 402 before anything is queued.

| Endpoint (`/api/images…`) | |
|---|---|
| `GET` · `GET queue` · `GET options` · `GET health` | Status, the server's options and health, prices, the account, the line · the line alone |
| `POST jobs` · `GET jobs` · `GET jobs/{id}` · `GET jobs/{id}/preview` · `DELETE jobs/{id}` | Jobs (202 with the job) |
| `POST preprocess` | `{preprocessor, image, params}` → the control map (PNG) |
| `GET gallery?q=&starred=1&job=&before=` · `GET gallery/{id}[/file?download=1]` · `PATCH gallery/{id}` · `DELETE gallery[/{id}]` | The gallery |
| `PUT key` · `GET pricing` · `PUT pricing` | The server's key (administrators); picture prices (`perImage`, `perGpuMinute`) |

### Songs and the Music tab

The **Music** tab (under Workspace) makes songs with **Suno** through the Suno API wrapper (`Music`; the FastAPI `server.py` in `Desktop\suno`, with `/generate`, `/clips/status`, `/clip/{id}/audio`, `/lyrics`, `/credits`, `/clips`, `/health`). Everything needs the **Music (Suno)** permission (`music.suno`).

- **The wrapper.** It runs as the `SunoAPI` scheduled task (at startup, as Administrator with S4U, no password stored) through `run-server.cmd`, which restarts it after 10 s if it stops and keeps `server.log` under 10 MB. It listens on `127.0.0.1:8000` (`SUNO_HOST`, `SUNO_PORT`). Its Suno session token lasts about an hour and is renewed over HTTP from the saved Clerk `__client` cookie (`auth_state.json`, valid for about a year); when that cookie expires, run `login.py` again. Every route except `/health` and `/docs` needs its key (`X-API-Key` or `Bearer`): `api_key.txt` next to `server.py`, made on first start (`SUNO_API_KEY` wins; `off` turns the check off).
- **Setup.** `Music.BaseUrl` is the wrapper (`http://127.0.0.1:8000`). Its key never reaches a browser: an administrator pastes `api_key.txt` on Music → Settings, which keeps it in the vault as `Music.ApiKeySecret` (`suno-api-key`), usable by no agent. `Music.ApiKey` (or `AGENTIC_Music__ApiKey`) wins over the vault.
- **Create.** *Describe it*: a description (up to 3,000 characters) and Suno writes the lyrics. *Write it*: a title, a style (genre, mood and voice chips, or any tags), and lyrics with `[Verse]`/`[Chorus]`/… sections, which **Write lyrics with AI** fills from a description (Suno's lyrics model). Either can be **instrumental**. The model is `Music.DefaultModel` (`chirp-goose`, v6 mini) or any Suno model id. Each request makes **two songs**.
- **Requests.** Suno is asked once and followed here (`/clips/status` every `PollSeconds`), not by the page: the page shows each song's state; the songs reach the library even when the page was closed, and requests that had not ended are followed again after a restart (`MusicJobs`). An account may have `MaxActiveJobsPerUser` (2) requests at once; a request ends after `JobTimeoutSeconds` (900), keeping the songs that were finished.
- **Audio: downloaded on first play.** Suno scrambles the stream its own player plays (`media_urls` with an `encoding`), so the only playable file is Suno's official download (the wrapper's `GET /clip/{id}/download`: `POST /api/download/authorize`, then `GET /api/download/clip/{id}?format=mp3`). Each song's first download uses one of the Suno account's downloads (a free account has a few; more come with a paid plan or download packs), so a song is downloaded the first time it is played or downloaded here, then kept; songs nobody plays use none. When none are left the play says so (402 `SUNO_NO_DOWNLOADS`). Administrators see the downloads left on Music → Settings. Audio kept before 2.0.8 (the scrambled stream) is dropped at start, so its next play downloads it properly.
- **Library.** Every song is kept here (`MusicSongs`, `data\music`: its cover, its audio once played (an MP3), the newest `MaxSongsPerUser` (300) per account; starred ones always) with its title, style, lyrics and model, so it plays after Suno's links expire. A player bar plays the library or the request in turn; songs can be searched, starred, renamed, downloaded, deleted, and reused as a new request. Administrators also see the Suno account's own songs and can take them into their library.
- **Pricing.** Per song made and per lyrics written by AI, set by administrators on Music → Settings, and by administrators and *Manage money* on Finance (`MusicPricing`; `Music.PricePerSong` / `PricePerLyrics` are the starting prices). Charged when the songs are kept, as `Music` ledger entries with the model's name and the account's surcharge; songs Suno failed are not charged. A User account whose balance does not cover two songs is refused with 402 before Suno is asked. Suno's own credits (shown to administrators) are spent either way.

| Endpoint (`/api/music…`) | |
|---|---|
| `GET` | Status, the wrapper's state, prices, models (and Suno's credits for administrators) |
| `POST jobs` · `GET jobs` · `GET jobs/{id}` | Requests (202 with the request) |
| `POST lyrics` | `{instruction}` → lyrics written by Suno |
| `GET songs?q=&starred=1&job=&before=` · `GET songs/{id}[/audio?download=1 \| /cover]` · `POST songs/{id}/fetch` · `PATCH songs/{id}` · `DELETE songs[/{id}]` | The library (`PATCH`: `{title, starred}`; `fetch`: download the song's audio from Suno now, what its first play does) |
| `GET suno` · `POST import` · `PUT key` | The Suno account's songs, taking them in, the wrapper's key (administrators) |
| `GET pricing` · `PUT pricing` | Music prices: `perSong`, `perLyrics` |

### Prices and the Money tab

The **Money** tab (Workspace, after Music) tracks crypto prices and exchange rates. The server reads them on its own and keeps them for the charts, so they are there whether or not anyone has the page open. Every account can look.

- **Crypto.**
  - Source: CoinMarketCap's public simple-price API (`Markets.CryptoUrl`), USD per coin, about every minute (`CryptoPollSeconds`, 60).
  - API key: without one, CoinMarketCap allows only a few anonymous calls, then answers 429 (error 1022). Set `Markets.CryptoApiKey` (or `AGENTIC_Markets__CryptoApiKey`) to a free CoinMarketCap key to raise that limit; it is sent as `X-CMC_PRO_API_KEY`.
  - Fallback: while CoinMarketCap refuses, prices come from Coinbase's public exchange rates (`CryptoFallbackUrl`, no key), and the page says so. CoinMarketCap is asked again after 30 minutes; the pause doubles each time it refuses, up to 6 hours.
- **Exchange rates.**
  - Source: Frankfurter (`FxUrl`), the European Central Bank's reference rates, as units of each currency per 1 USD (`FxBase`).
  - Read every `FxPollMinutes` (60). The ECB publishes once each working day.
  - A currency watched for the first time gets its last `FxHistoryDays` (90) at once.
- **What is watched.**
  - `CryptoSymbols` and `FxQuotes` are the starting lists; empty means BTC, ETH, LTC, XRP, USDT and EUR, GBP, AUD, CNY.
  - Administrators change the lists on the tab. The change is kept in HostState and wins over the configuration.
- **The page.**
  - Ticker cards show the price, the 24-hour change and a sparkline; the price flashes when it changes (every 15 s).
  - The large chart has ranges 1H, 6H, 24H, 7D, 30D, 90D, 1Y and All, round price steps, the last price marked, a crosshair with price, change and time, and open, high, low, last and change.
  - Two tables list every price with its changes over 1 hour, 24 hours, 7 days and 30 days.
- **Kept.**
  - Prices are stored in `MarketQuotes` (migration 0037).
  - Every price is kept `KeepAllDays` (14); after that, the first price of each hour; nothing older than `KeepDays` (365).
- **Agents and chats.**
  - Every agent has the read-only `market_prices` tool: prices, changes and an optional short series. A profile's `DeniedTools` can remove it.
  - Chats have it when **Money** is on in the composer (or `/money on`).

| Endpoint (`/api/markets…`) | |
|---|---|
| `GET` | Every watched asset: `price`, `change1h` (crypto), `change24h`, `change7d`, `change30d`, `utc`; the watch lists; `status` (last read, source, problems) |
| `GET history?kind=&symbol=&range=&points=` | `points: [[unix ms, price], …]` for a chart (at most `points`) |
| `POST refresh` | Read prices now (at most every 15 s) |
| `PUT watchlist` | Administrators: `{crypto: ["BTC", …], fx: ["EUR", …]}` |

### Stocks and the Stocks tab

The **Stocks** tab (Workspace, after Money) tracks stock and index quotes. Every account can look.

- **Quotes.**
  - Source: Yahoo Finance's chart API (`Stocks.ChartUrl`), no key. Most US prices are delayed about 15 minutes.
  - The server reads every watched ticker every `PollSeconds` (60) while a market is open, pre-market or after hours, and every `ClosedPollMinutes` (15) otherwise.
  - Each quote has the price, the change against the previous close, open, day range, volume, 52-week range, the market state, today's prices in 5-minute steps, and the change over 5 sessions and a month.
  - Quotes are not stored; charts of any range come from Yahoo when asked.
- **Tickers.**
  - Written as Yahoo writes them: `AAPL`, `BRK-B`, indexes with `^` (`^GSPC`, `^IXIC`, `^DJI`).
  - The lookup uses NASDAQ Trader's symbol directory (`NasdaqListedUrl`, `OtherListedUrl`): NASDAQ, NYSE and other US exchanges, read once a day. Test issues are left out.
  - Any ticker Yahoo knows can be charted, watched or not.
- **What you watch.**
  - Every account watches the defaults until it saves its own list: `Stocks.Symbols`, or when that is empty `^GSPC`, `^IXIC`, `^DJI`, AAPL, MSFT, NVDA, AMZN, GOOGL, META and TSLA.
  - Each account changes its own list on the tab (or **Watch it** / **Stop watching** on a ticker). Each ticker is checked with Yahoo first.
  - The list is kept with the account (`UserPreferences`, key `stocks.watchlist`). **Use the defaults** removes it; saving exactly the defaults does the same.
  - The server reads the defaults every round, and an account's own tickers for 30 minutes after it last opened the tab. A ticker not read lately is read when the tab asks for it.
- **The page.**
  - Index and stock cards show the price, today's change and the day's line, green or red against the previous close. Prices flash when they change (every 20 s).
  - The large chart has ranges 1D, 5D, 1M, 6M, YTD, 1Y, 5Y and Max. The 1-day chart draws the previous close as a dotted line and colours the day against it.
  - A table lists every quote with its change, 5-day and 1-month changes, day and 52-week ranges, and volume.
- **Agents and chats.**
  - Every agent has the read-only `stock_quotes` tool: quotes for up to 10 tickers (any ticker, watched or not) and an optional short history (5d, 1mo, 6mo, 1y or 5y). A profile's `DeniedTools` can remove it.
  - Chats have it when **Stocks** is on in the composer (or `/stocks on`). Asked without tickers, it answers with the chat user's own list; agents get the defaults.

| Endpoint (`/api/stocks…`) | |
|---|---|
| `GET` | Your tickers' quotes, `watchlist`, `custom` (your own list or the defaults), `defaults`, `status` (last read, problems, tickers in the directory) |
| `GET chart?symbol=&range=` | `points: [[unix ms, price], …]` for `1d` (default), `5d`, `1mo`, `6mo`, `ytd`, `1y`, `5y` or `max`; `previousClose` for `1d` |
| `GET quote?symbol=` | Any ticker's quote (404 `UNKNOWN_TICKER` when Yahoo does not know it) |
| `GET search?q=&limit=` | Tickers from the directory: symbols starting with `q`, then names containing it |
| `POST refresh` | Read the quotes now (at most every 15 s) |
| `PUT watchlist` | Your own list: `{symbols: ["^GSPC", "AAPL", …]}` |
| `DELETE watchlist` | Back to the defaults |

### Prediction markets and the Polymarket tab

The **Polymarket** tab (Workspace, after Stocks) shows Polymarket's prediction markets: what traders think will happen, as odds. Every account can look. Polymarket's APIs are public; no key is needed.

- **Sources** (`Polymarket.*`).
  - **Gamma** (`GammaUrl`): events and their markets, categories (tags), outcomes, prices, volume, liquidity, dates, condition and token ids, resolution status. Results are joined across Gamma's pages when more than one page is asked for.
  - **CLOB** (`ClobUrl`): best bid and ask, midpoint, last trade, the order book, and price history, by outcome token.
  - **Data API** (`DataUrl`, v2): a market's latest trades and biggest holders, any wallet's positions and activity, and the traders' leaderboard.
  - **Market channel** (`WebSocketUrl`): one connection from the server. It follows the busiest `LiveTopMarkets` (60) markets and every market open on someone's page or asked about by a tool, at most `MaxLiveTokens` (1500) outcomes. It carries books, price changes, best bid and ask, trades, new markets and resolutions.
- **The channel's upkeep.** The server sends PING every 10 s and reconnects after any failure or 40 s of silence (1 s, doubling to 60 s). It subscribes and unsubscribes as pages come and go. A token stays followed for 10 minutes after it was last wanted.
- **Closed markets.** Closed, inactive and resolved markets are left out unless **Closed & resolved** is chosen. That includes markets settled at 100%.
- **The page.**
  - **Markets:** event cards, busiest first, sortable by 24h volume, total volume, liquidity, ending soon or newest. Filter by category (Politics, Sports, Crypto, Finance, Geopolitics, Tech, Culture, World, Economy, Elections) or search. **Load more** fetches the next page.
  - **An event opened:**
    - Every market with chance, bid, ask, last trade and 24h volume.
    - For the market chosen: each outcome's chance and price, best bid and ask, spread, last trade, volumes, liquidity and end date.
    - A chart of the chance (1D, 1W, 1M, Max), the order book, recent trades, top holders and the rules.
    - A link to the market on polymarket.com.
  - **New:** markets the channel saw open while the server listened, and the newest events.
  - **Leaderboard:** top traders by profit or volume for a day, week, month or all time.
  - **Wallet:** any address's open positions (settled ones folded away) and activity. A trader's name anywhere on the tab opens their wallet.
- **Live prices on the page.** They come from the server's own stream (`GET /api/polymarket/stream`, server-sent events), fed by the channel, never by asking Polymarket again.
  - The stream sends changed prices each second, the open order book when it changes, and the channel's status every 15 s.
  - Each account has at most 3 streams. Pictures come through the server, and only from Polymarket's own storage.
- **Agents and chats.**
  - Every agent has the read-only `polymarket` tool. Its actions are `search` (words, category, open or closed, up to 100 events), `market` (live bid, ask, midpoint, last trade, the top of the book, optional history), `trades`, `holders`, `leaderboard` and `wallet`. A profile's `DeniedTools` can remove it.
  - Chats have it when **Polymarket** is on in the composer (or `/polymarket on`).

| Endpoint (`/api/polymarket…`) | |
|---|---|
| `GET` | The channel's status, the categories, markets that opened while the server listened |
| `GET events?q=&category=&status=&sort=&offset=&limit=` | Events with their open markets (`prices` are each outcome's chance, live where followed) |
| `GET events/{id}?closed=1` | One event with all its markets (closed ones too with `closed=1`) |
| `GET history?token=&range=` | An outcome's price over `1d`, `1w`, `1m` or `max` |
| `GET book?token=&depth=` | An outcome's order book (live when followed) |
| `GET trades?market=` · `GET holders?market=` | A market's latest trades, its biggest holders (by condition id) |
| `GET leaderboard?period=&sort=` | Top traders: `day`, `week`, `month`, `all`; `pnl` or `volume` |
| `GET wallet?address=` | A wallet's positions and activity |
| `GET stream?events=&book=` | The page's live prices (server-sent events) |

### News and the News tab

The **News** tab collects articles from RSS and Atom feeds every `News.PollMinutes` (10). Each article is kept once, by its link, for `KeepDays` (30) in `NewsArticles` with its outlet, category, summary, author and picture.

- **Feeds.** With `News.Feeds` empty, the built-in list is used:
  - **International:** BBC World, Al Jazeera, Deutsche Welle, France 24, NHK World, CBC World, Euronews, South China Morning Post.
  - **US:** NPR, PBS NewsHour, New York Times, Washington Post, Fox News, CBS News, NBC News, The Guardian US.
  - **UK:** BBC UK, Sky News, The Guardian UK, The Independent.
  - **Europe:** BBC Europe, The Guardian Europe, Politico Europe, DW Europe, RTÉ News, Kyiv Independent, The Local.
  - **Australia:** ABC News Australia, The Guardian Australia, Sydney Morning Herald, The Age.
  - **News agencies:** Reuters and the Associated Press, through Google News (neither has a public feed of its own).
  - **Investigative:** ProPublica, The Intercept, The Conversation, Bellingcat.
  - **Business:** CNBC, Bloomberg, WSJ Markets, Financial Times, MarketWatch.

  Your own list, each entry `{Name, Url, Category, Enabled}`, replaces the built-in one.
- **How feeds are read.**
  - Formats: RSS 2.0, RSS 1.0/RDF and Atom.
  - Feeds are fetched as a browser would be; some sites (CBC) answer nothing else.
  - The Sources tab shows each feed's last read, how many items it had, how many were new, and its error. Politico Europe (its own feed answered 429 after a few reads), NHK World and the Washington Post are read through Google News: on 2026-11-02 NHK's feed address answered 404 (its English JSON list had stopped updating in September), and feeds.washingtonpost.com answered 502 for every feed.
- **The page.**
  - Tabs by category, with counts.
  - Search over headlines and summaries (every word must match), outlet and time filters.
  - Articles are grouped by day, with their picture.
  - "N new articles" appears instead of the list jumping.
  - Older articles load on request.
- **Pictures.** They come through the server (`GET /api/news/{id}/image`), because the page may only load its own pictures. Only https links on public hosts are fetched, each redirect is checked, pictures are limited to 3 MB, and the last 300 are cached.
- **Agents and chats.**
  - Every agent has the read-only `news_articles` tool: search by words, category, outlet and hours. Results are marked untrusted.
  - Chats have it when **News** is on in the composer (or `/news on`). The articles join the answer's sources and are cited as [n].

| Endpoint (`/api/news…`) | |
|---|---|
| `GET ?q=&category=&source=&hours=&limit=&before=` | Articles, newest first, with counts per category and outlet |
| `GET sources` | The feeds and how their last read went |
| `GET {articleId}/image` | The article's picture |
| `POST refresh` | Read every feed now (at most once a minute) |

### Speech, pictures and songs from programs

Everything the Audio, Images and Music tabs do is also an API, used with a personal API key (Accounts → API key) sent as `x-api-key: agk_…` or `Authorization: Bearer agk_…`. No cookie or CSRF token is needed. A key acts as its account: the same permissions (`chat.voice`, `images.sd`, `music.suno`; a User account also needs *Personal API key*), the same prices and balance, and the same lines for the servers. `/swagger` describes every route of the three, with each body field and its range, examples and what each route answers.

```bash
# Speech: a WAV file
curl -H "x-api-key: $KEY" -H "Content-Type: application/json" -d '{"text":"Hello","voiceKey":"speaker:Ana Florence"}' https://host/api/audio/tts -o hello.wav
# Pictures: a job (202), followed until done, then each picture
curl -H "x-api-key: $KEY" -H "Content-Type: application/json" -d '{"prompt":"a lighthouse at dusk","steps":25}' https://host/api/images/jobs
curl -H "x-api-key: $KEY" https://host/api/images/jobs/{jobId}            # status, inLine, queuePosition, imageIds
curl -H "x-api-key: $KEY" https://host/api/images/gallery/{imageId}/file -o picture.png
# Songs: a request (202, two songs), followed until done, then each song
curl -H "x-api-key: $KEY" -H "Content-Type: application/json" -d '{"prompt":"a calm piano song"}' https://host/api/music/jobs
curl -H "x-api-key: $KEY" https://host/api/music/jobs/{jobId}             # status, songIds
curl -H "x-api-key: $KEY" https://host/api/music/songs/{songId}/audio -o song.m4a
```

### Custom models

**Custom models** (under Platform) are one model name that sends each request to the model an administrator chose for that kind of work. Every user with **Chat & model API** can use them, in Chat and through `/v1`.

- **Routes.** For each kind of work, pick a model, optional fallbacks (tried in order on 404/429/5xx), extra keywords, a temperature, a max-tokens default and instructions added to the system prompt. Requests no route claims go to the **default model**.
- **Detected from the request itself** (checked first): images (vision), JSON output (`response_format`), tool calling, and long prompts (above `LongContextTokens`).
- **Detected from what is asked:** coding, debugging, code review, DevOps, data & SQL, maths, reasoning, research, summarization, writing, creative writing, marketing, translation, business, science, teaching, extraction, brainstorming, role-play and conversation.
- **Sorting modes:**
  - **Rules** use keywords and are instant.
  - **Classifier** asks a fast model which category applies.
  - **Hybrid** (the default) uses keywords when they are clear, and the classifier otherwise.
- **Try it** shows which model would answer a prompt with the routes as currently edited. The page also shows usage per model, user and route.
- **Responses** keep the custom model's name and add `X-Agentic-Model`, `X-Agentic-Route` and `X-Agentic-Route-Reason` headers. Embeddings with a custom model's name go to its `EmbeddingModel`.

### API keys

Every account can generate a personal API key from the user menu → **API key** (or `POST /api/auth/api-key`).

- The key (`agk_…`) is shown once; only its SHA-256 hash is stored. Generating a new key replaces the old one, and **Revoke** deletes it.
- Administrators see each user's key prefix and last use under **Users & access** and can revoke any key.
- Send the key as `Authorization: Bearer agk_…` or `x-api-key: agk_…`. It needs no CSRF token.
- **A key acts with its account's role, permissions and agent scope, exactly like the dashboard:** a request the account could not make in the dashboard returns 403. Changes to the account (permissions, disabling) apply to the key immediately.

### OpenAI-compatible API

`/v1` (also `/api/v1`) speaks the OpenAI API. Any OpenAI SDK or tool (LibreChat, Continue, LangChain …) works with base URL `http://<host>:<port>/v1` and an API key. It needs the **Chat & model API** permission.

| Endpoint | Notes |
|---|---|
| `GET /v1/models`, `GET /v1/models/{id}` | Custom models first, then every model of the OpenWebUI instance |
| `POST /v1/chat/completions` | The full request (tools, `response_format`, images, `stream`, …) is passed through. Custom models are routed first. Streaming responses are relayed as server-sent events as they arrive. |
| `POST /v1/completions` | Legacy text completions, converted to chat (streaming too) |
| `POST /v1/embeddings` | Provider models, or a custom model's embedding model |
| `POST /v1/images/generations` | The images API in `ImageGeneration` (`gpt-image-2`, then its fallbacks); `n` up to 4. A `model` that is not one of those (a client's default `dall-e-3`) gets the configured model. |

```python
from openai import OpenAI
client = OpenAI(base_url="http://127.0.0.1:8080/v1", api_key="agk_...")
for chunk in client.chat.completions.create(model="agentic/assistant", stream=True,
        messages=[{"role": "user", "content": "Write a SQL query for monthly revenue"}]):
    print(chunk.choices[0].delta.content or "", end="")
```

Usage (user, endpoint, model, routed model, route, tokens, duration, errors) is recorded in `ModelApiUsage`.

### Desktop app (client tools)

The Agentic desktop app (`Agentic_desktop`) shows this web app through a local proxy that adds the user's API key, and gives Chat tools that run on the user's own computer: their Claude Code skills and slash commands, local MCP servers, and file and command tools with approval prompts.

The protocol behind this is generic. A Chat message may include:
- `clientTools`: OpenAI function definitions.
- `clientInstructions`: text added to the system prompt.

Desktop apps also connect to `GET /api/chat/stream?device=…` and publish what they offer with `POST /api/chat/devices/{id}/catalog`. As a result, a chat that uses a desktop app's tools keeps them when you continue it in the web app, as long as the desktop app is open. The desktop app runs the calls, and the web app shows its name next to the model.

- **One device per project.** Each VS Code workspace is its own device (`vscode-<install>-<workspace hash>`, named "PC · VS Code · Project"), like each CLI folder already was. Before, every VS Code window shared one id, so a chat could get another window's workspace description and tools, and its calls ran in that other project.
- **One window per call.** The stream's `hello` names its connection (`subscriber`); a tool call for a device goes to exactly one of its windows (`data.target`; the most recently connected), never to all of them, so the side bar and an editor tab of one workspace, two desktop app windows or two CLIs in one folder never both run it. A window that drops its stream gets the call again when its page reconnects (`&page=` in the stream URL; the page runs each call id once); another window of the device only after 20 s.

When the model calls one of these tools:
1. The answer stream emits a `client_tool` event (`{id, name, arguments}`).
2. The client runs the tool and posts `POST /api/chat/conversations/{id}/tool-results {id, ok, result}`.
3. The answer continues with that result. It waits as long as the computer running the tool stays connected (a build or test run can take long; the app ends a command at its own timeout), up to `Chat.ClientToolTimeoutSeconds` (14400 = 4 h). If that computer disconnects, the call fails after 3 minutes without it.

**Commands on the user's computer.** The desktop app and VS Code offer `run_command`, `run_command_background`, `command_output` and `kill_command`; the CLI offers `Bash` (its `timeout` works the same way), `BashOutput` and `KillShell`. A command is never stopped for taking long: after its wait (default 120 s) it goes on in the background and the model gets its id and output so far; `command_output` / `BashOutput` show whether it still runs, for how long, how long since its last output, its new output, and warn when it seems to wait for input or to be stuck (`wait_seconds` waits for it). A call returns as soon as the shell ends even if something the command started keeps its output open (that used to hang a call until the server gave up after 15 minutes). On Windows each command has a job object of its own, so `kill_command` / `KillShell` also stop what outlived the shell.

In the browser no client tools are sent, so nothing changes there.

### Client app updates (desktop app and CLI)

The server hands out the newest desktop app and CLI, for Windows and for Linux. Each platform has its own channel: `cli` and `desktop` for Windows, `cli-linux-x64` and `desktop-linux-x64` for Linux. Installed copies check their channel and update themselves:
- The CLI checks when an interactive session starts, and when you run `agentic update`.
- The desktop app checks when it starts and every `Updates.CheckIntervalMinutes` (360).
- Both send their version and their exe's SHA-256. A copy is outdated when the server has a newer version, or the same version with another SHA-256 (a rebuild).
- They check every download against the server's SHA-256 before they use it.
- A newer exe is used from the next start. The desktop app shows a **Restart now** prompt.

The builds live in `Updates.Directory`, which is `updates` next to `appsettings.json` by default:

```
updates\cli\agentic.exe                          Windows
updates\desktop\AgenticClient.exe
updates\desktop\www\desktop\...                 the desktop app's www\desktop folder, mirrored into every install
updates\cli-linux-x64\agentic                    Linux (the same projects built with -r linux-x64)
updates\desktop-linux-x64\AgenticClient
updates\desktop-linux-x64\www\desktop\...
updates\<folder>\release.json                  { "notes", "minimumVersion" }; Linux builds also need "version"
```

A Linux binary has no version resource the server could read, so its `release.json` names the version. `publish-clients.ps1` writes it.

`tools\publish-clients.ps1` builds both apps as single-file programs for Windows and Linux and copies them there. It can run while the server runs:

```
.\tools\publish-clients.ps1                                  # both apps, both platforms, into publish\updates
.\tools\publish-clients.ps1 -Platforms linux-x64             # only the Linux builds
.\tools\publish-clients.ps1 -Apps cli -Notes "Faster startup"
.\tools\publish-clients.ps1 -UpdatesDir D:\Agentic\updates -Apps desktop -Version 0.3.0
```

The desktop app's files are handled like this:
- When only `www\desktop` changes, the files are replaced at once and the app offers to reload the page.
- When the exe changes too, the new exe brings its own files when it starts.
- Files the package no longer has are deleted from the install.
- `config.json`, `mcp.json` and `settings.json` are never handed out, even if they are in the folder.

**Apps & updates** in the dashboard lists the published builds, one card per app with a row per platform. Every user can download them.
- **Windows builds** download as a zip, for example `Agentic-Desktop-0.4.0.zip`. It holds the exe and its folders (the desktop app's `www\desktop`), laid out as the app keeps them: extract it to a folder of its own and run the exe.
- **Linux builds** download as a `.tar.gz`, for example `Agentic-CLI-2.2.0-linux-x64.tar.gz`. It holds one folder (`Agentic-CLI/`, `Agentic-Desktop/`) with the binary already executable. The server builds each zip once per build and keeps it in `data\cache\update-zips`. Installed copies keep updating themselves from the exe and the single files. The list of installs is in the API (`/api/updates/installs`, View system).

| Endpoint | |
|---|---|
| `GET /api/updates` | Every published app: version, SHA-256, size, notes, files, `zipUrl` |
| `GET /api/updates/{app}?version=&sha256=&installId=&machine=` | Is this install outdated? `updateAvailable`, `mandatory` (below `minimumVersion`), `reason`, `latest`. Records the install |
| `GET /api/updates/{app}/zip` | The whole app as a zip, for a new install (`X-Sha256` header) |
| `GET /api/updates/{app}/download` | The exe, which automatic updates use (`X-Sha256` header) |
| `GET /api/updates/{app}/files/{path}` | One file of the package, e.g. `www/desktop/desktop.js` |
| `GET /api/updates/installs?days=90` | Installs seen, each `current` or `outdated` (View system) |

Any signed-in account or API key may check and download.

### Swagger

**Swagger** (under Platform) shows the whole API in Swagger UI, generated from the live route table (`GET /api/openapi.json`, OpenAPI 3.0). Each operation lists the permission it needs (`x-permission`). **Try it out** runs requests as the signed-in user.

JSON API (all under `/api`; errors are returned as `{"error":{"code","message"}}`):

```
POST /api/auth/login | /api/auth/logout      GET /api/auth/me        GET /healthz (anonymous)
GET  /api/status
GET  /api/agents[?details=1&includeSubagents=1]   POST /api/agents ({profile, grants})   GET|DELETE /api/agents/{id}
GET  /api/agents/{id}/definition   GET /api/meta/agent-form
GET  /api/agents/{id}/tasks[?status=queue|Pending,…]   GET /api/agents/{id}/tasks/{taskId}[?transcript=1]
GET  /api/agents/{id}/memory|activity|messages|communications|tools|ai-usage|ledger|schedules|artifacts
POST /api/agents/{id}/start|stop|pause|resume|restart|run-now|cancel-task|task|message|profit|adjust|daily-limit
POST /api/agents/{id}/tasks/{taskId}/retry|hold|release|clone|cancel|move   PATCH /api/agents/{id}/tasks/{taskId}
POST /api/agents/{id}/tasks/bulk ({action: cancel|hold|release|retry|top|bottom, taskIds})
GET  /api/subagents   GET /api/agents/{id}/subagents   POST /api/agents/{id}/subagents/{subagentId}/cancel
GET  /api/models/custom   POST /api/models/custom   PUT|DELETE /api/models/custom/{key}   GET /api/ai/available-models
GET  /api/model-grants[?agent=&model=&history=1]   GET /api/agents/{id}/model-access   PUT|DELETE /api/agents/{id}/model-grants/{key}
GET  /api/operations   POST /api/operations/pause-all|resume-all|emergency-stop   GET /api/stats/hourly
GET|POST /api/agents/{id}/schedules ({task fields, repeat})   PATCH|DELETE /api/agents/{id}/schedules/{scheduleId}   POST .../{scheduleId}/run
GET|POST /api/global-tasks   GET /api/global-tasks/{id}   POST /api/global-tasks/{id}/approve|replan|cancel
PATCH|DELETE /api/global-schedules/{id}   POST /api/global-schedules/{id}/run
GET  /api/learning/summary   GET /api/learning/studies[?agent=&outcome=&status=]   POST /api/learning/studies ({agentId, outcome, note})
GET  /api/learning/studies/{id}   POST /api/learning/studies/{id}/rerun
GET  /api/learning/lessons[?q=&status=&outcome=&agent=&study=]   PATCH /api/learning/lessons/{id} ({title, body, tags, status, pinned})
GET  /api/tasks[?agent=&status=&q=&offset=]  /api/communications  /api/communications/topology  /api/correlations/{id}
POST /api/messages/{id}/cancel
GET  /api/models   POST /api/models/{key}/reset-circuit   GET /api/ai/status|calls   POST /api/ai/check
GET  /api/mcp      POST /api/mcp/{id}/reconnect
GET  /api/finance  /api/finance/ledger  /api/pricing      PUT /api/pricing/{key}
GET  /api/approvals   POST /api/approvals/{id}/resolve
GET  /api/config/validate|summary  /api/audit  /api/events/recent  /api/events/stream (SSE)
GET  /api/metrics   (Prometheus text format; read scope)
POST /api/auth/password ({currentPassword, newPassword})
GET|POST /api/users   PATCH|DELETE /api/users/{username}   POST /api/users/{username}/password   (administrators)
GET  /api/artifacts/{id}/content
GET|POST|DELETE /api/auth/api-key   DELETE /api/users/{username}/api-key
GET|POST /api/custom-models   POST /api/custom-models/test   GET /api/custom-models/usage   GET|PUT|DELETE /api/custom-models/{id}
GET  /api/chat/meta   GET|POST /api/chat/conversations   GET|PATCH|DELETE /api/chat/conversations/{id}
POST /api/chat/conversations/{id}/messages (SSE events: user, start, route, status, sources, reasoning, delta, tool, error, done, title)
POST /api/chat/run ({language: python|node|bash, code})   GET /api/openapi.json
POST /api/chat/conversations/{id}/tool-results ({id, ok, result}: a client tool's result)   GET /api/favicon/{host}
GET  /api/chat/stream[?device=&name=] (SSE: live runs, approvals, desktop apps)   GET /api/chat/devices   POST /api/chat/devices/{id}/catalog
GET  /api/chat/conversations/{id}/live   POST /api/chat/conversations/{id}/approvals ({id, choice: once|always|deny})
GET  /api/chat/conversations/{id}/context   POST /api/chat/conversations/{id}/compact
GET  /v1/models   POST /v1/chat/completions|completions|embeddings|images/generations   (also under /api/v1)
```

Example (API token with the `submit-task` scope):

```powershell
curl -H "Authorization: Bearer <token>" -H "Content-Type: application/json" `
     -d '{"name":"Weekly report","description":"Summarize this week\u0027s results","priority":"Normal"}' `
     http://127.0.0.1:8080/api/agents/ManagerAgent/task
```

---

## 10. Operations

**Deployment.** Run `dotnet publish src\Agentic\Agentic.vbproj -c Release -r win-x64 -o C:\Agentic`. This produces one self-contained `Agentic.exe`; the native SQLite library is embedded and extracted at start. Copy `appsettings.json` next to it. The SQLite database, artifacts, backups and logs live under `data\` and `logs\`, relative to the config file.

**Windows Service.**
```powershell
sc.exe create Agentic binPath= "C:\Agentic\Agentic.exe --service --config C:\Agentic\appsettings.json" start= delayed-auto
sc.exe failure Agentic reset= 86400 actions= restart/60000/restart/60000/restart/300000
setx /M AGENTIC_AIProviders__OpenWebUI__ApiKey "sk-..."
sc.exe start Agentic
```
In service mode, the bootstrap password is not visible because the service has no console. Configure `Admin.Users` before you install.

**Graceful shutdown.** Ctrl+C, a service stop or a system shutdown runs these steps in order:

1. Stop accepting HTTP commands.
2. Stop scheduling.
3. Stop accepting new delegations and requests (replies still flow).
4. Signal the agents and let current operations finish within `ShutdownTimeoutSeconds`.
5. Cancel whatever is still running. Those tasks keep their checkpoints and resume after restart.
6. Close MCP connections.
7. Flush events, statistics and logs.
8. Stop HTTP.
9. Close the database.

**Recovery.**

- **On restart:** interrupted tasks are re-queued and resume from their checkpoints. Pending and processing messages are kept, and orphaned ones are reset. Waits and deadlines are restored. Usage limits are re-seeded from the last 24 hours.
- **Agent faults:** an agent's loop restarts with exponential backoff. After `MaxRestartAttempts` faults within `RestartWindowMinutes`, the agent is marked **Unhealthy**, an alert fires, and it stays stopped until an administrator starts it.
- **Stuck work:** stuck tasks (no progress for `StuckAfterSeconds`) are cancelled and retried, and hung loops are restarted. The host process itself is never terminated.

**Backup.** An online, consistent SQLite backup runs every `Retention.BackupIntervalHours` into `data\backups`, keeping `BackupCount` copies. A backup is also taken automatically before every schema migration. To restore, stop the host and copy a backup over `data\agentic.db`. You can also copy `data\` while the host is stopped.

**Database maintenance.** Retention runs every hour:

- Activity is purged after 14 days.
- AI and tool payloads are purged after 30 days; their metrics are kept 180 days.
- Message contents are purged after 30 days; message metadata is kept 180 days.
- Finished tasks are purged after 90 days; polling tasks that completed normally (a health check every minute) after 3 days (`PollingTaskDays`). The history and kept conversations of purged tasks go with them.
- Routine events (an agent going Idle ↔ Running ↔ Waiting, a polling task created, started and finished normally) are shown live but not kept in the activity history.
- Audit entries are purged after 365 days.
- The raw evidence of learning case studies is dropped after 180 days (`CaseStudyEvidenceDays`); reports and lessons are kept.
- The ledger is **never** purged.

Each retention run is followed by `PRAGMA incremental_vacuum` and `PRAGMA optimize`. The database uses WAL mode, a busy timeout, and a bounded number of concurrent operations.

**Monitoring.** `GET /api/metrics` serves Prometheus text format. It needs an API token with the `read` scope; a token's agent allowlist also limits the per-agent series. It covers:

- agents by state, running subagents, open tasks by status, pending approvals
- per model: slots in use, capacity, queue length, circuit state, calls, failures, latency
- spillover and escalation counters
- cloud spend today against the global cap
- per wallet: available balance, spend today, burn rate, runway, lifetime spend and profit
- MCP connectivity, lessons and case studies, dropped events

```yaml
scrape_configs:
  - job_name: agentic
    metrics_path: /api/metrics
    authorization: { credentials: "<api token with read scope>" }
    static_configs: [{ targets: ["agentic.internal:8080"] }]
```

Useful alerts include `agentic_wallet_runway_days < 1`, `agentic_model_circuit_open == 1` and `agentic_approvals_pending > 0` for longer than an hour.

**Troubleshooting.**

| Symptom | Check |
|---|---|
| `HttpListenerException: Access is denied` | Bind to `127.0.0.1`, run as Administrator, or add a URL ACL (see Security). |
| Agents never use cloud models | Prices are missing (Finance → Model pricing), the balance or daily limit is exhausted (Budget tab), or the model is not in the agent's `AllowedModels`. |
| A model shows circuit **Open** | The provider is failing for that model (e.g. "no available channel"). Fallbacks are in use. Run `--probe-models`, or use **Reset** on the Models page. |
| Tasks stay **Waiting** | The task is waiting on another agent's reply or on the admin Inbox. The deadline and wait keys are in the task details. |
| `LOOP_DETECTED` / `DELEGATION_DEPTH_EXCEEDED` | Working as designed. Adjust the agents' instructions or the limits in `Communication`. |
| Local model slow | Watch the slot gauge and the queue; raise `LocalModels.Slots` if your server handles more requests in parallel. Raise priorities, allow spillover by pricing a cheap model, or reduce agents' scheduled AI work. |
| Logs | Files are `logs\agentic-YYYYMMDD.log` (JSON lines, rotated, secrets redacted). Use the dashboard's Live events and Audit pages. |

---

## 11. Development and tests

```powershell
dotnet build Agentic.slnx
dotnet test  Agentic.slnx
```

The xUnit suite (`tests/Agentic.Tests`) runs against a scriptable fake AI provider and a temporary SQLite database. It covers:

- **Agents:** lifecycle, cancellation, scheduling, deterministic handlers, restart recovery including the non-idempotent tool case
- **Isolation:** memory and artifact isolation, API-shape checks, tool permission filtering, host refusal of unpermitted tools
- **Communication:** delivery, permission rejection, delegation lineage, A→B→C→A loop rejection, depth, rate, size and priority limits, idempotency, request/response with park and resume, message persistence
- **Models:** model selection, escalation (automatic and agent-requested, bounded by tier), spillover only when affordable, circuit breakers, priority gate ordering and aging
- **Economy:** reserve/settle, insufficient funds, daily limits, profit idempotency, image charges, burn rate and runway forecast with the low-runway warning in prompts
- **Learning:**
  - a broke agent is studied, and another agent sees the lesson in its prompt
  - a profitable agent's study falls back to rules when the model's report is unusable
  - approval, retraction, pinning and re-analysis
  - repeated conclusions confirm existing lessons instead of duplicating them, and re-analysis does not double-count
  - case-study evidence retention
  - report parsing
  - the learning API and profit-to-task attribution
- **Metrics:** the Prometheus endpoint (auth, format, series)
- **MCP:** the real SDK client against the sample stdio server, MCP permissions, a failing MCP server
- **HTTP API:** auth, CSRF, scopes, token agent allowlists, error shape, CSP, audit
- **Configuration validation:** including the shipped `appsettings.json`

Projects use `Option Strict On`, `Option Explicit On` and `Option Infer On` throughout. The code avoids `Thread.Sleep`, uses async/await with `CancellationToken`, never holds locks across I/O, and keeps all queues bounded.

---

## License

Agentic is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](../LICENSE) for the full text.

Third-party files in `src/Agentic/Dashboard/vendor/` keep their own licenses: KaTeX (MIT, `vendor/katex/LICENSE.txt`) and Swagger UI (Apache-2.0, `vendor/swagger/LICENSE`).
