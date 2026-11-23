# NexusAgent — agent framework for NexusShell

NexusAgent is the AI agent built into NexusShell, on top of **Microsoft Agent Framework 1.28**
(`Microsoft.Agents.AI`). It gives NexusShell one agent that brings together:

- **Mixture of Experts (MoE)**: a gate picks the best specialist experts for each request.
  They run in parallel and a synthesizer merges their answers.
- **Sub-agents**: experts delegate to other experts through tools (`ask_<expert>`).
- **Skills**: Agent Skills folders (`SKILL.md`), loaded on demand.
- **MCP servers**: stdio or HTTP, configured with the same `mcpServers` format as Claude Desktop and Cursor.
- **Tools**: plugin tool sets (system, filesystem, shell, web, and the app's own `terminal` set). Risky tools ask the user first.
- **Local and online models**: Ollama and LM Studio, plus OpenAI, Azure OpenAI, Anthropic, Gemini, Groq, OpenRouter, DeepSeek, Mistral and any other OpenAI-compatible server.
- **Secrets only in user environment variables**: no keys in code or in configuration files.

## Clean architecture

```
NexusShell.App (WPF, presentation)
   │  AIChatWindow "Agent mode", Agent keys window, approval dialog, terminal tool
   ▼
NexusShell.Agents.Infrastructure      adapters: providers, MCP, skills, tools, config, secrets
   │
   ▼
NexusShell.Agents.Application         use cases: MoE agent, router, experts, sub-agents,
   │                                  middleware, approval loop, conversations + PORTS
   ▼
NexusShell.Agents.Domain              pure model: providers, models, experts, routing,
                                      MCP definitions, tool risk, secret references
```

**Dependency rule:** every arrow points inward, and the compiler enforces it because each layer is its own project.
Domain has no package references at all. Application only knows Agent Framework abstractions and its own
**ports** (`Application/Abstractions/*`). Infrastructure implements those ports. The App only calls
`services.AddNexusAgents()` and talks to the `INexusAgentService` facade.

### Folder map

| Layer | Folder | Contents |
|---|---|---|
| Domain | `Providers/` `Models/` | `ProviderProfile`, `ProviderKind`, `ModelReference` (`provider/model`, `auto`) |
| | `Experts/` `Routing/` `Orchestration/` | `ExpertProfile`, `RoutingDecision`, `OrchestrationSettings` |
| | `Mcp/` `Tools/` `Secrets/` | `McpServerDefinition` (+ validation), `ToolRisk`/`ToolApprovalPolicy`, `SecretReference`, `SecretTemplate`, `SecretPatterns` |
| | `Memory/` `Models/` | `MemoryEntry`, `MemorySettings`, `ModelContextWindows` (context window per model) |
| | `Chats/` `Scheduling/` | `ChatSummary`, `SavedChat`; `ScheduledTask`, `TaskSchedule` (next-run calculation) |
| | `Connectors/` | `ConnectorDefinition`, `ConnectorField`, `ConnectorAbility`, `ConnectorState` |
| | `Configuration/` | `NexusAgentConfiguration` (aggregate root) |
| Application | `Abstractions/` | Ports: `ISecretStore`, `IChatClientFactory`, `IModelProviderAdapter`, `IProviderAvailability`, `IAgentToolProvider`, `IMcpToolProvider`, `ISkillsProviderFactory`, `IToolApprovalHandler`, `IAgentConfigurationProvider` |
| | `Routing/` | MoE gate: `KeywordRoutingGate`, `LlmRoutingGate`, `ExpertRouter` (hybrid), `RoutingMath` (sparse top-k) |
| | `Orchestration/` | `MixtureOfExpertsAgent` (a real `AIAgent`), `MixtureOfExpertsSession`, `ResponseSynthesizer`, `AgentOrchestratorFactory` |
| | `Agents/` | `ExpertAgentFactory` (pipeline builder), `InlineToolApprovalAgent`, `RecentMessagesChatReducer` |
| | `SubAgents/` `Middleware/` `Models/` `Tools/` | `SubAgentTool`, `ToolInvocationMiddleware`, `ModelResolver`, `ToolCatalog` |
| | `Conversations/` `Progress/` | `INexusAgentService` facade, `AgentRunScope` (progress + approvals) |
| | `Memory/` | `IAgentMemory` (`AgentMemory`), `MemoryRanker` (BM25), `MemoryContextProvider` (Agent Framework `AIContextProvider`), `MemoryToolProvider` |
| | `Context/` | `ContextBudget`, `TokenEstimator`, `CompactingChatReducer`, `ConversationSummarizer` |
| | `Chats/` `Scheduling/` | `IChatLibrary` (save, rename, pin, duplicate, export, move), `IAgentScheduler` (`AgentScheduler`), `ScheduleParser`, `ScheduleToolProvider` |
| | `Connectors/` | `IConnector` (port), `IConnectorService`, `McpConnectorPresets`, `IMcpSettingsStore` (port) |
| Infrastructure | `Providers/` | `OpenAICompatibleProviderAdapter`, `AnthropicProviderAdapter`, `OllamaProviderAdapter`, `ChatClientFactory`, `ProviderAvailability` |
| | `Mcp/` | `McpToolProvider` (lazy connections, `${env:}` expansion, approval classification) |
| | `Skills/` | `FileSkillsProviderFactory` (Agent Framework `AgentSkillsProvider`), `SkillScriptRunner` |
| | `Tools/<Category>/` | `SystemInfo`, `FileSystem`, `Shell`, `Web` tool providers |
| | `Configuration/` `Experts/` | JSON DTOs, merge and validation, front-matter parser, Markdown expert loader, user folder scaffold |
| | `Secrets/` | `EnvironmentVariableSecretStore`, `SecretRedactor` |
| | `Memory/` | `JsonMemoryRepository` (`%AppData%\NexusShell\agent\memory\memories.json`) |
| | `Chats/` `Scheduling/` | `JsonChatStore` (one folder per chat; deleting goes to the Recycle Bin), `JsonScheduledTaskStore` (`agent\schedule\tasks.json`) |
| | `Connectors/` | `OAuthAccount` (browser sign-in, PKCE, loopback), `ConnectorHttp`, `Google/` (Gmail, Calendar), `Microsoft/` (Outlook mail, calendar), `GitHub/`, `UserMcpSettingsStore` |
| | `Agent/` | **Bundled content**: `agent.json`, `instructions.md`, `experts/*.md`, `skills/*/SKILL.md` |

## How a request flows (MoE mode)

1. **Gate.** `ExpertRouter` scores every routable expert. In hybrid mode (the default) the score is
   75% from the LLM gate (a small model that returns JSON weights) and 25% from the keyword gate
   (offline and Unicode-aware, so Arabic keywords work too). If the LLM gate fails, the keyword gate is used alone.
   `RoutingMath.SelectTopK` keeps experts above `minScore`, drops experts scoring under half of the winner, and stops at `topK`.
2. **Experts in parallel.** Each selected expert is created lazily by `ExpertAgentFactory`:
   ```
   IChatClient (provider adapter + OpenTelemetry)
     → ChatClientAgent (instructions, tools, MCP tools, sub-agent tools, skills,
                        long-term memory, history sized from the model's context window)
     → function middleware (audit, progress, secret redaction)
     → OpenTelemetry agent
     → InlineToolApprovalAgent (human-in-the-loop)
   ```
   Each expert keeps a **private session**. An expert that joins a conversation late first receives
   the shared turns it missed ("catch-up").
3. **Combine.** If one expert answered, its answer is returned unchanged with no extra model call.
   If several answered, `ResponseSynthesizer` merges them weighted by gate score. If no synthesizer
   model is available, the answers are merged without a model.
4. **Fallbacks.** If every selected expert fails, the default expert is tried. If no model is
   available, the user gets a clear message (set a key or start Ollama) instead of a crash.

### Model resilience (providers retire and overload models)

- `ProviderErrorChatClient` (Infrastructure) translates every SDK error (OpenAI `ClientResultException`, Anthropic, Ollama, HTTP) into a provider-neutral `ModelProviderException`. Each one has a kind (`ModelNotFound`, `Unauthorized`, `RateLimited`, `Unavailable`) and a readable message, so there's no more raw `Service request failed`.
- When a model is **not found** (404, or a "decommissioned" 400) or **overloaded** (503), the expert is rebuilt on the next model. Order: the same provider's `defaultModel`, then its `fallbackModels`, then other providers. `IModelHealthRegistry` keeps the bad model out of use for 30 minutes (not found) or 2 minutes (overloaded).
- Routing uses the `fast` model keyword, which resolves to each provider's `fastModel` (for example `gemini-3.5-flash-lite`). Routing decisions stay quick even when the main model is a slow thinking model.
- `ThoughtSignatureHandler` keeps Gemini 3.x tool calling working through the OpenAI-compatible endpoint. Gemini requires each tool call's `thought_signature` to be sent back in the next request, and the OpenAI SDK drops it, so the handler captures the signature and re-attaches it. It does nothing for other providers.
- SDK retries are turned off (OpenAI-compatible and Anthropic), so a failing model hands over to the next one immediately instead of waiting through a long backoff.

Other modes (`orchestration.mode`):
- `coordinator`: the coordinator expert gets every other expert as an `ask_*` sub-agent tool and decides itself when to delegate.
- `sequential`: an Agent Framework workflow (`AgentWorkflowBuilder.BuildSequential`) runs `orchestration.sequence` as a pipeline (for example code, then review).

## Long-term memory and the context window

Goal: the agent answers from facts it was told or verified, not from guesses, and long
conversations keep fitting the model without losing what was said.

**Long-term memory** (`Memory/`):
- Every expert gets four tools: `remember`, `recall_memory`, `update_memory` and `forget_memory`. They only touch the agent's own memory file, so they run without approval and stay available in read-only mode.
- Before every model call, `MemoryContextProvider` recalls the relevant memories with `MemoryRanker` and adds them to the instructions. `MemoryRanker` uses BM25 (offline, works in English and Arabic), boosted by verification, recency and use; pinned memories always come along. Each memory is shown with its id, how it was learned (the user said it, verified with a tool, or the agent's own unverified note) and its date, followed by the rules: cite memories, re-check facts that can change, the user wins on conflicts, never save secrets.
- `AgentMemory` enforces these rules:
  - short facts only;
  - credentials refused (`SecretPatterns` plus known secret values);
  - near-duplicates merged;
  - the least useful unpinned memories evicted beyond `maxEntries`.
- Memories can be global or tied to the working folder (a project).
- The user reviews everything in the chat's Memory window, or with `/remember`, `/memory` and `/forget`.

**Context window** (`Context/`):
- `ModelContextWindows` knows each model's window: the provider's `contextWindow`, then the model family (Gemini 1M, GPT-4.1 1M, GPT-5 400K, Claude 200K, and so on), then a safe default. For Ollama the window is also requested as `num_ctx`; Ollama's small default otherwise silently cuts prompts.
- `ContextBudget` splits the window: answer reserve, history 45%, catch-up 10%, memories 5%, attachments 25%, instructions and tools 15%.
- `CompactingChatReducer` keeps each expert's history within its budget. When it overflows, the oldest turns are summarized into notes by the `fast` model (`ConversationSummarizer`: facts, decisions, paths, commands, errors, open tasks, and nothing invented) and the recent turns are kept word for word. If summarizing fails, old turns are dropped as before. Tool calls are never separated from their results.
- Catch-up turns and attachments are sized from the same budget, so large-context models read whole projects and small local models are never overflowed.

The shared house rules (`Agent/instructions.md`, "Accuracy") tell every expert to answer only from the conversation, memory, attachments and tool results, and to say "I don't know" instead of guessing.

## Saved chats and scheduled tasks

**Saved chats** (`Chats/`):
- Every chat is saved after each answer, in one folder per chat under the chats folder (default `%AppData%\NexusShell\chats`; the user can move it):
  - `chat.json`: title, dates, pinned, the agent choices;
  - `messages.json`: what the window shows;
  - `agent-state.json`: the agent's own session, serialized by `MixtureOfExpertsAgent` with the shared transcript and each expert's history and summaries.
- Reopening a chat passes that state to `StartConversationAsync(..., ConversationResume)`, so the conversation or task continues with full context. If the state cannot be read, the saved turns are replayed instead.
- Secret values are masked before anything is written (`SecretPatterns.Mask` plus the redactor).
- Titles come from the first question until the user renames the chat.
- Deleting sends the folder to the Recycle Bin.

**Scheduled tasks** (`Scheduling/`):
- A task is a prompt the agent sends itself at a time: once, every N minutes (at least 5), daily, weekdays, or chosen weekdays, in the user's time zone. `TaskSchedule.NextAfter` handles daylight-saving gaps.
- `AgentScheduler` checks every 20 seconds while NexusShell is open and runs due tasks one at a time. Each run is saved as a chat ("Scheduled: name"), either a new chat per run or one continued chat.
- Runs missed while NexusShell was closed happen once at startup, or are marked missed (per task).
- Runs are **unattended** (`ConversationOptions.Unattended`): `InlineToolApprovalAgent` declines every action that needs approval instead of opening a dialog nobody may answer, and the answer says what still needs the user.
- The agent can create, list and cancel tasks with `schedule_task`, `list_scheduled_tasks` and `cancel_scheduled_task`. Creating and cancelling always ask the user, and a scheduled run can never schedule more work.

## Connectors (Gmail, Google Calendar, Outlook, GitHub, and MCP apps)

**Built-in connectors** (`Infrastructure/Connectors`) give every expert tools while the connector is connected (`IConnectorService.ActiveToolCategories`):

| Connector | Sign-in | Read (no approval) | Change (always asks) |
|---|---|---|---|
| Google | OAuth: your own Google Cloud "Desktop app" client; scopes `gmail.readonly`, `gmail.compose`, `calendar.events` | `gmail_search`, `gmail_read`, `google_calendar_list_events` | `gmail_create_draft`, `gmail_send`, `gmail_reply`, `google_calendar_create_event`, `google_calendar_delete_event` |
| Microsoft | OAuth: your own Entra app registration (public client, no secret); `Mail.ReadWrite`, `Mail.Send`, `Calendars.ReadWrite` | `outlook_search_mail`, `outlook_read_mail`, `outlook_list_events` | `outlook_create_draft`, `outlook_send_mail`, `outlook_reply`, `outlook_create_event`, `outlook_delete_event` |
| GitHub | Fine-grained personal access token in `GITHUB_TOKEN` | repositories, issues, pull requests, search, notifications | `github_create_issue`, `github_comment` |

How sign-in works (`OAuthAccount`, following RFC 8252 for desktop apps):
- The provider's own page opens in the user's default browser; NexusShell never sees the password.
- The answer returns to a one-time loopback listener (127.0.0.1 or ::1, random port).
- PKCE (S256) and a state value protect the exchange.
- Only the refresh token is kept, in a user environment variable (`NEXUS_GOOGLE_REFRESH_TOKEN` or `NEXUS_MICROSOFT_REFRESH_TOKEN`), so the secret redactor keeps it away from models. Access tokens stay in memory.
- A revoked sign-in is forgotten and the user is asked to sign in again.

How connector content and actions are kept safe:
- Mail, issue and event text is returned with a note that it is outside content: data, not instructions.
- Recipients are validated and subjects kept on one line, so mail headers cannot be injected.
- Repository names are validated.
- Scheduled runs are unattended, so they can read and summarize but never send.

**MCP connectors** (`McpConnectorPresets`) are templates for apps without a built-in connector: GitHub's official remote MCP server, Notion, Jira and Confluence (`uvx mcp-atlassian`), Slack, Playwright browser automation, and Context7 documentation.
- Adding one writes an entry to the user's `mcp.json` with `${env:NAME}` references only, `"allExperts": true` (every expert gets its tools) and `"trustReadOnlyHints": true`; anything else still asks.
- The window checks that the needed runtime (npx, uvx) is installed.
- On Windows, launchers that are not `.exe` (such as `npx`) run through `cmd.exe /c`.

## Security model

| Concern | How it is handled |
|---|---|
| API keys and tokens | Only **environment variable names** appear in configuration (`apiKeyEnv`, `endpointEnv`, `${env:NAME}`). Values are read at call time from process, then user, then machine scope, and saved to **user** scope by the Agent keys window. They are never logged. |
| Literal secrets in config | Rejected at load time: providers containing `apiKey`/`token`/... fields are disabled, and MCP env, headers or arguments with literal credentials disable that server. A warning explains the fix. |
| Key sent in clear text | Remote providers must use `https`. `http` is allowed only for `local: true` servers. |
| Exfiltration through tools | `ToolInvocationMiddleware` redacts the values of credential-like environment variables from every tool result **before** it reaches the model. Terminal context and user messages are redacted too. |
| Risky actions | `ToolRisk` + `ToolApprovalPolicy`: read-only tools run without asking. Network, mutating and destructive tools ask the user in an accessible dialog (Allow once / Allow for this conversation / Deny; Esc denies, and Enter never approves). `run_shell_command` and MCP tools always ask unless the user configured otherwise. The default for headless hosts is **deny all**. |
| Credential files | `read_text_file` and `search_files` refuse private keys, `.env`, certificates and password vaults. |
| Secrets in memory | `remember` and the Memory window refuse anything that looks like a key, token, private key or password assignment, and the value of any credential environment variable. |
| Saved chats | Messages are masked (credential-shaped text and known secret values) before they are written. Chat ids are checked, so a path cannot escape the chats folder. |
| Scheduled runs | They are unattended, so actions needing approval are declined, never taken. Creating a schedule always asks the user. Prompts containing secrets are refused. |
| Connectors | Sign-in happens in the user's browser (OAuth with PKCE, loopback redirect, state check). Refresh tokens and API tokens live only in user environment variables and are redacted from model traffic. Sending, creating and deleting always ask. External content is marked as data. Mail headers cannot be injected. |
| SSRF | `fetch_url` allows only http(s) to **public** IPs. The IP is checked again on every redirect and at socket connect time, which blocks DNS-rebinding attacks. |
| Skill scripts | Off by default (scripts are not even listed to the model). When enabled, every run still asks for approval. |
| Prompt injection | House rules tell experts to treat web, file, MCP and command output as data. Approvals and redaction enforce this in code. |

## Configuration (no code changes needed to scale)

Bundled defaults live in `<app>\Agent\`. User additions and overrides live in `%AppData%\NexusShell\agent\`, which is created on first run with a README.

| What you want | Do this |
|---|---|
| Add an expert | Drop `experts/<id>.md` (front-matter: `name`, `description`, `model`, `keywords`, `tools`, `skills`, `mcp`, `subagents`, `temperature`, `routable`; the body holds the instructions). |
| Override or disable an expert | Use the same file name in the user folder. `enabled: false` turns the expert off. |
| Add a skill | `skills/<name>/SKILL.md` (Agent Skills format). Reference it from an expert's `skills:` list. |
| Add an MCP server | Add it to `mcp.json` under `mcpServers`. Secrets go in as `${env:NAME}`. Opt in from an expert with `mcp: [name]`. |
| Use a local model | Start Ollama (`ollama pull llama3.2`), LM Studio, llama.cpp, vLLM or Jan. They are already in `models.preference` and their installed models are found automatically. |
| Give a model more context | Set the provider's `"contextWindow"` in tokens. For Ollama this is also the `num_ctx` it is asked for: larger means a longer memory and more RAM/VRAM. |
| Tune memory | Use the `memory` section: `enabled`, `maxEntries`, `maxEntryChars`, `recallTopK`, `recallBudgetTokens`, and `compactHistory` (summarize old turns instead of dropping them). |
| Use an online model | Set its key in the Agent keys window, or with `setx` in a terminal (variable name, then the key). `auto` picks the first available provider in `models.preference`. |
| Pin a model per expert | Add `model: anthropic/claude-sonnet-5-5` (or `ollama/qwen2.5-coder`, and so on) to the expert's front-matter. |
| Survive model retirements | Give a provider `"fallbackModels": [...]` (tried in order) and a quick `"fastModel"` for routing. |
| Make routing free and private | Set `orchestration.router.model` to a small local model, or set `strategy` to `keyword`. |

## Extending in code

| Extension | Steps |
|---|---|
| New provider protocol | Implement `IModelProviderAdapter` and register it with `TryAddEnumerable`. |
| New tool category | Implement `IAgentToolProvider` (one class in `Tools/<Category>/`) and call `services.AddAgentToolProvider<T>()`. The App's `TerminalToolProvider` is an example. |
| Different secret backend | Replace `ISecretStore`, for example with Windows Credential Manager or Azure Key Vault. |
| Different approval UI | Register your own `IToolApprovalHandler` before `AddNexusAgents()`. |
| New orchestration | Implement it as an `AIAgent` and add a case to `AgentOrchestratorFactory`. |

## Patterns used

Clean Architecture / Ports and Adapters, Strategy (provider adapters, routing gates), Composite
(tool catalog), Decorator / Chain of Responsibility (Agent Framework middleware pipeline, approval
agent), Factory (expert agent factory, orchestrator factory), Facade (`INexusAgentService`),
Lazy initialization (experts, sub-agents, MCP connections), Mixture of Experts with sparse top-k
gating, Null Object / secure default (`DenyAllToolApprovalHandler`), Ambient Context
(`AgentRunScope` via `AsyncLocal`), and Options + overlay configuration.

## Tests

`NexusShell.Agents.Tests` has 85 tests: unit tests per layer, plus integration tests that run the
**real Agent Framework pipeline** with only the model scripted. The integration tests cover:

- MoE fan-out and synthesis
- single-expert pass-through
- approve and deny paths, and "allow for this conversation"
- secret redaction of tool results
- sub-agent delegation
- late-joiner catch-up
- session serialization
- coordinator and sequential (workflow) modes
- skills (progressive disclosure, scripts never run when disabled)
- unavailable-model fallback

```bash
dotnet test NexusShell.sln -c Release
```
