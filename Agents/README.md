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
| | `Mcp/` `Tools/` `Secrets/` | `McpServerDefinition` (+ validation), `ToolRisk`/`ToolApprovalPolicy`, `SecretReference`, `SecretTemplate` |
| | `Configuration/` | `NexusAgentConfiguration` (aggregate root) |
| Application | `Abstractions/` | Ports: `ISecretStore`, `IChatClientFactory`, `IModelProviderAdapter`, `IProviderAvailability`, `IAgentToolProvider`, `IMcpToolProvider`, `ISkillsProviderFactory`, `IToolApprovalHandler`, `IAgentConfigurationProvider` |
| | `Routing/` | MoE gate: `KeywordRoutingGate`, `LlmRoutingGate`, `ExpertRouter` (hybrid), `RoutingMath` (sparse top-k) |
| | `Orchestration/` | `MixtureOfExpertsAgent` (a real `AIAgent`), `MixtureOfExpertsSession`, `ResponseSynthesizer`, `AgentOrchestratorFactory` |
| | `Agents/` | `ExpertAgentFactory` (pipeline builder), `InlineToolApprovalAgent`, `RecentMessagesChatReducer` |
| | `SubAgents/` `Middleware/` `Models/` `Tools/` | `SubAgentTool`, `ToolInvocationMiddleware`, `ModelResolver`, `ToolCatalog` |
| | `Conversations/` `Progress/` | `INexusAgentService` facade, `AgentRunScope` (progress + approvals) |
| Infrastructure | `Providers/` | `OpenAICompatibleProviderAdapter`, `AnthropicProviderAdapter`, `OllamaProviderAdapter`, `ChatClientFactory`, `ProviderAvailability` |
| | `Mcp/` | `McpToolProvider` (lazy connections, `${env:}` expansion, approval classification) |
| | `Skills/` | `FileSkillsProviderFactory` (Agent Framework `AgentSkillsProvider`), `SkillScriptRunner` |
| | `Tools/<Category>/` | `SystemInfo`, `FileSystem`, `Shell`, `Web` tool providers |
| | `Configuration/` `Experts/` | JSON DTOs, merge and validation, front-matter parser, Markdown expert loader, user folder scaffold |
| | `Secrets/` | `EnvironmentVariableSecretStore`, `SecretRedactor` |
| | `Agent/` | **Bundled content**: `agent.json`, `instructions.md`, `experts/*.md`, `skills/*/SKILL.md` |

## How a request flows (MoE mode)

1. **Gate.** `ExpertRouter` scores every routable expert. In hybrid mode (the default) the score is
   75% from the LLM gate (a small model that returns JSON weights) and 25% from the keyword gate
   (offline and Unicode-aware, so Arabic keywords work too). If the LLM gate fails, the keyword gate is used alone.
   `RoutingMath.SelectTopK` keeps experts above `minScore`, drops experts scoring under half of the winner, and stops at `topK`.
2. **Experts in parallel.** Each selected expert is created lazily by `ExpertAgentFactory`:
   ```
   IChatClient (provider adapter + OpenTelemetry)
     → ChatClientAgent (instructions, tools, MCP tools, sub-agent tools, skills, bounded memory)
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
- SDK retries are reduced to 1, so a failing model hands over to the next one quickly instead of waiting through a long backoff.

Other modes (`orchestration.mode`):
- `coordinator`: the coordinator expert gets every other expert as an `ask_*` sub-agent tool and decides itself when to delegate.
- `sequential`: an Agent Framework workflow (`AgentWorkflowBuilder.BuildSequential`) runs `orchestration.sequence` as a pipeline (for example code, then review).

## Security model

| Concern | How it is handled |
|---|---|
| API keys and tokens | Only **environment variable names** appear in configuration (`apiKeyEnv`, `endpointEnv`, `${env:NAME}`). Values are read at call time from process, then user, then machine scope, and saved to **user** scope by the Agent keys window. They are never logged. |
| Literal secrets in config | Rejected at load time: providers containing `apiKey`/`token`/... fields are disabled, and MCP env, headers or arguments with literal credentials disable that server. A warning explains the fix. |
| Key sent in clear text | Remote providers must use `https`. `http` is allowed only for `local: true` servers. |
| Exfiltration through tools | `ToolInvocationMiddleware` redacts the values of credential-like environment variables from every tool result **before** it reaches the model. Terminal context and user messages are redacted too. |
| Risky actions | `ToolRisk` + `ToolApprovalPolicy`: read-only tools run without asking. Network, mutating and destructive tools ask the user in an accessible dialog (Allow once / Allow for this conversation / Deny; Esc denies, and Enter never approves). `run_shell_command` and MCP tools always ask unless the user configured otherwise. The default for headless hosts is **deny all**. |
| Credential files | `read_text_file` and `search_files` refuse private keys, `.env`, certificates and password vaults. |
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
| Use a local model | Start Ollama (`ollama pull llama3.2`), or set LM Studio's `defaultModel` and add it to `models.preference`. |
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
