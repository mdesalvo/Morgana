# Morgana — Multi-Agent Multi-Channel Conversational AI Framework

## Before writing any C#

**Load the `code-commentation` skill first, in full, at the start of every coding activity in this
repository** — before the first edit, not after it and again in any later turn that resumes coding.
Unconditional: a new file, a refactor and a single changed statement alike. Having read it earlier in
the session does not waive it. The observed failure mode is exactly that — writing first, then
retrofitting comments once somebody points at an uncommented body.

## Where the rationale lives

This file is the map: what exists, where it is, what breaks if you get it wrong. It is deliberately
not the argument. **Every design decision is argued in the XML doc of the type that carries it** —
`Records.cs`, `Constants.cs`, each service, each actor, `Program.cs`'s numbered sections. Before
changing something whose reason is not obvious, open that type and read its `<remarks>`: the reason
is there, in more detail than this file could hold.

## What is Morgana

A conversational AI framework on **.NET 10**, **Akka.NET** (actor model) and **Microsoft.Agents.AI**.
It orchestrates specialized agents that classify, route and resolve user inquiries, with content
moderation, shared context, tool calling and channel adaptation.

Domain experts model agents **declaratively** — prompt and tool contracts in JSON, a thin C# class —
package them as plugin DLLs and the framework handles orchestration, streaming, persistence, guard
rails and observability.

## Design Philosophy — agents are prose, not code

The framework code (Akka pipeline, tool loop, intent routing, channel adaptation) is not where
day-to-day work happens. A domain agent *is* its prompt configuration — its entry in `agents.json`
read together with the global policies in `morgana.json`. Building or tuning an agent is ~95%
authoring **clear, non-contradictory, precise prose**: every sentence is dispositive — an instruction
an LLM executes, not documentation.

The characteristic defect is not an exception, it is a **logical contradiction** between two
instructions read together — typically **emergent and non-local**: one clause in the agent against
one in the global policies. Prefer structural fixes over point patches: state a unifying doctrine
high in the policy order (low `Priority`, so it renders first) and let the specific policies read as
instances of it. That shrinks the contradiction surface instead of chasing symptoms.

Two things *outside* the prose can sabotage a correct prompt and are worth ruling out first because
they are invisible from `agents.json`:
- **Model tier** — a dense, layered prompt needs a capable model; `Efficiency` amplifies
  contradiction-following failures where `Performance` would not.
- **Rendering / channel code** — a rich-card leaf showing raw `**` is a Razor bug in Cauldron,
  unfixable from any prompt.

**Never lower a harness threshold to make a scenario pass.** A regression there is a contradiction in
the text; the text is what goes back.

## Solution Structure

```
Morgana/
  Morgana/                 # working directory
    Morgana.Contracts/     # zero-dependency wire contracts (NuGet)
    Morgana.AI/            # core framework library (NuGet)
    Morgana.Web/           # ASP.NET Core host
    Directory.Build.props  # shared build settings, version, dependencies
  Channels/                # Cauldron (Blazor), Grimoire (rich TTY), Rune (poor TTY)
  Examples/                # example plugin: Billing, Contract, Inventory, Monkey agents
  PromptHarness/           # live non-regression harness (xUnit v3)
  Alembic/                 # Blazor authoring workbench: AI interview to a whole domain
```

**Eight solutions, one per unit.** Only the framework's own projects live in `Morgana.slnx`; the
plugin, the channels, the harnesses and the workbench each stand alone and reach it by project
reference. That is what keeps every one of them replaceable by a customer's own.

Each unit outside `Morgana/` has its **own `CLAUDE.md`** — read it before working there. `Examples/`
is the exception and is described below, having none.

### Examples (the plugin, no `CLAUDE.md` of its own)

**One organization, several desks**: not three unrelated demos but three roles inside one fictional
shop, *The Greenhouse & Nursery*, on **one** SQLite system of record (`Data/Examples.db`, seeded and
rebased onto the current month at deployment). Each desk writes only its own competence, yet the
books stay consistent whichever desk closes a sale: `InventoryAgent`'s order confirmation and
`ContractAgent`'s plan enrolment are the two dispositive actions and both bill through the identical
shared path, `GreenhouseDatabaseHelper.BillCustomerAsync`. `BillingAgent` is read-only — every line
on its books was written by another desk.

The plugin also exists to show two structural things: the desks **consult each other**
(`Billing → Inventory`, `Contract → Billing`, deliberately a chain and not a triangle, so the
refusal of a second hop is exercised too) and `MonkeyAgent` is the one agent whose tools are
acquired at runtime from an MCP server, with an empty context vocabulary.

### Morgana.AI

| Folder | Purpose |
|---|---|
| `Abstractions/` | `MorganaActor`, `MorganaAgent`, `MorganaLLM`, `MorganaTool`, `MorganaHostedAgent` (the `AIAgent` publishing an intent over A2A) |
| `Actors/` | `ConversationManagerActor`, `ConversationSupervisorActor`, `GuardActor`, `ClassifierActor`, `RouterActor` |
| `Adapters/` | `MorganaAgentAdapter` (agent builder, peer-consultation surface), `MorganaToolAdapter` (tool to `AIFunction`), `MorganaChannelAdapter` (rich to plain degradation) |
| `Attributes/` | `[HandlesIntent]`, `[RequiresLLMTier]`, `[ProvidesToolForIntent]`, `[UsesMCPServer]`, `[ConsultsAgent]` |
| `ChatClients/` | `IChatClient` decorators: `TierDefaultsChatClient`, `DustAccountingChatClient`, `MorganaAnthropicClient` |
| `Interfaces/` · `Services/` | Every service contract and its default implementation |
| `Providers/` | `MorganaAIContextProvider` (context variables plus the shared registry), `MorganaChatHistoryProvider` |
| `SessionStores/` | `MorganaHostedAgentSessionStore` — which conversation an inbound A2A request is served on |
| `Telemetry/` | `MorganaTelemetry`, holding its own span and attribute glossary |
| `Records.cs` | Every immutable record: actor messages, configuration, DTOs |
| `Constants.cs` | The glossary: **every literal that is a contract between two parties who cannot see each other**, `PromptProperties` included. Deliberately absent: log text, prompt prose, `IConfiguration` keys. The test is a *resolver*, not a mention |
| `morgana.json` | Framework prompts: Morgana, Classifier, Guard, Presentation, ChannelAdapter |

### Morgana.Web

| File | Purpose |
|---|---|
| `Program.cs` | Full DI wiring. **Deliberately linear and un-extracted** — the boot *order* is load-bearing (plugins before the registry's checks, the actor system after DI, cards projected before Kestrel binds) and reading it top to bottom is the only thing that declares it |
| `Extensions/A2APublicationExtensions.cs` | `AddMorganaA2A` / `MapMorganaA2AAsync` — the one feature whose halves must straddle `builder.Build()` |
| `Controllers/MorganaController.cs` | REST at `api/morgana` |
| `Hubs/MorganaHub.cs` | SignalR at `/morganaHub` |
| `Filters/A2AAuthenticationFilter.cs` | The controller's own auth gate, applied to the A2A JSON-RPC endpoints, fail-closed. The card endpoint stays open by design |
| `Services/PluginLoaderService.cs` | Scans `plugins/` for `MorganaAgent` subclasses |
| `Services/KestrelHostAddressService.cs` | Reports the address Kestrel actually bound, so a card names a callable endpoint with nothing configured |
| `Services/SignalRChannelService.cs` | Pushes messages and stream chunks over SignalR |

## Architecture

### Actor hierarchy (per conversation)

```
ConversationManagerActor       # entry point, lifecycle, channel metadata
  ConversationSupervisorActor  # FSM orchestrator
    GuardActor                 # content moderation
    ClassifierActor            # intent classification
    RouterActor                # intent to agent routing, then the domain agents
```

Actor naming: `/user/{suffix}-{conversationId}`. Agent identifier: `{agent_name}-{conversation_id}`.

### Turn pipeline (FSM states)

1. **Idle** — waits for `UserMessage`
2. **AwaitingGuardCheck** — LLM policy check. Fail: rejection, stay idle
3. **AwaitingClassification** — LLM classification, falling back to `"other"`. *Skipped when an
   active agent exists.* Candidates colliding within `IntentCollisionThreshold` divert here: a
   disambiguation quick reply goes straight to the user and the turn returns to Idle
4. **AwaitingAgentResponse / AwaitingFollowUpResponse** — the agent runs its tool loop and streams.
   The wait here is a budget on **silence**, not on the turn: every chunk renews it and so does
   `AgentStillWorking` on updates carrying no text — without it a consultation reads as a dead agent
5. Back to **Idle** — the response is forwarded through `IChannelService`

### REST API

| Endpoint | Method | Purpose |
|---|---|---|
| `conversation/start` | POST | Validates `ChannelMetadata` (required), creates the manager actor |
| `conversation/{id}/end` | POST | Stops the supervisor |
| `conversation/{id}/resume` | POST | 404 if unknown; restores the active agent |
| `conversation/{id}/message` | POST | Auth, then rate limit, then dust budget, then `UserMessage` |
| `conversation/{id}/history` | GET | `ConversationHistoryResponse` |
| `health` | GET | Actor system liveness |

Every endpoint authenticates through `AuthenticateRequestAsync` (Bearer JWT, fail-closed).

### Multi-turn and shared context

An agent signalling `IsCompleted = false` — declared via `SetTurnContinuation` or implied by quick
replies or a rich card — is remembered as `activeAgent`; later messages skip classification.

Tool parameters marked `Shared: true` route their values into a conversation-scoped `shared_context`
registry (first-write-wins, `INSERT OR IGNORE`). Every agent merges it at the start of each turn, so
a `customerCode` given to Billing reaches Contract without re-asking.

### Inter-agent consultation (A2A)

`[ConsultsAgent("billing")]` becomes one more `AIFunction` in the declaring agent's tool list, named
`consult_{intent}`. Underneath it is the A2A protocol end to end, on Microsoft's own stack. A second
argument names a **partner** publishing that colleague — `[ConsultsAgent("shipping", "acme")]`
becomes `consult_acme_shipping`. Where a colleague runs is deployment; the prose never says.

What must be known before touching it:

- **Publication is whole or nothing.** With `Morgana:AgentToAgent:Enabled`, *every* agent here is
  published, whether or not a sibling consults it — what an installation offers is what it can
  answer, never a side effect of its own topology. Switched off, **nothing** is stood up: no hosted
  agent, no route, no card, not one extra prompt token.
- **The gate is the partner, never the topology.** An agent that must not be answerable by a caller
  is kept from it by that partner's `InboundPolicy`, never by declining to publish it.
- **A caller is a channel or a partner, never both.** `Morgana:Authentication:Issuers` holds channels
  (who carries people); `Morgana:AgentToAgent:Partners` holds partners (who carries agent work).
  There is no `Type` field: what a caller is follows from the list its key was found in. A name in
  both is startup-fatal. This installation's own ring signs under the reserved issuer `morgana`,
  with a key coined at every start and configured nowhere.
- **The card is read before anything is signed**, so it may only send this installation where it
  already was: every advertised interface must share the origin the card was fetched from.
- **Two types exist only to reconcile one mismatch** — A2A hosting wants one long-lived agent per
  name, Morgana's agents are per-conversation actors. `MorganaHostedAgentSessionStore` decides which
  conversation an exchange is served on: a partner's is namespaced under the issuer the filter
  proved, never the `contextId` the caller wrote. `MorganaHostedAgent` owns no model and `Ask`s the
  actor the router would have reached.
- **The exchange leaves no trace.** The answering side runs on an ephemeral session nothing is
  written to; the asking side strips the `consult_*` call and its result from its own history once
  read, as a matched pair.
- **Nothing is served on credit.** Behind this door a request reaches an agent with none of the
  guard, classifier and channel rate limit a user's path goes through, so `IPeerAdmissionService`
  bounds how many conversations a partner may **open** — the one limiter that **fails closed**.

Everything else — the two-phase resolution, the middleware guards, what an answer costs and how it
settles, the card's security literals — is argued in `ConfigurationAgentDirectoryService`,
`MorganaAgentAdapter`, `MorganaHostedAgent`, `A2AAuthenticationFilter` and `Program.cs` section 9.5.

OTel: a `morgana.consultation` span nested under the answering agent's work.

## Service Layer

Extension points follow one pattern: interface in `Interfaces/`, default implementation in
`Services/`, registration in `Program.cs`.

| Service | Interface | Purpose |
|---|---|---|
| `LLMClassifierService` | `IClassifierService` | LLM intent classification; falls back to `"other"` at confidence 0 |
| `LLMGuardRailService` | `IGuardRailService` | LLM policy check. **Fails open** |
| `LLMPresenterService` | `IPresenterService` | Welcome message and quick replies. Never throws |
| `ConfigurationPromptResolverService` | `IPromptResolverService` | Two-tier resolution: framework prompts from `morgana.json`, domain from `agents.json`. Throws if one ID is declared in both |
| `ConfigurationPromptComposerService` | `IPromptComposerService` | Assembles everything the model reads: the fenced two-layer prompt, tool descriptions, the per-turn held-context declaration, the colleagues declaration, a colleague's question |
| `ConfigurationAgentDirectoryService` | `IAgentDirectoryService` | Both halves of A2A discovery, plus `ValidateTrustConfiguration` and `ValidatePublishedAddress` |
| `EmbeddedAgentConfigurationService` | `IAgentConfigurationService` | Merges every plugin's `agents.json`. Refuses a duplicated intent or prompt id and the reserved name `other` |
| `HandlesIntentAgentRegistryService` | `IAgentRegistryService` | Discovers agents by attribute; bidirectional intent validation; validates `[ConsultsAgent]` |
| `RequiresLLMTierValidationService` | `ILLMTierValidationService` | Every agent must declare a tier the active provider configures |
| `ProvidesToolForIntentRegistryService` | `IToolRegistryService` | Discovers tools; warns on orphans; errors on duplicates |
| `MCPClientRegistryService` | `IMCPClientRegistryService` | MCP connection pool keyed by URI or `stdio:{command}` |
| `SQLiteConversationPersistenceService` | `IConversationPersistenceService` | Per-conversation SQLite: encrypted session BLOBs, the shared-context registry |
| `SQLiteRateLimitService` | `IRateLimitService` | Sliding window per minute, hour and day. **Fails open** |
| `SQLiteDustLimitService` | `IDustLimitService` | Owns **every** dust question asked anywhere — no caller does the arithmetic itself. Thresholds 70%, 90%, lockout. **Fails open** |
| `SQLitePeerAdmissionService` | `IPeerAdmissionService` | Conversations a partner may open per hour. The one ledger that is not a conversation's (`morgana-peers.db`). **Fails closed** |
| `JWTAuthenticationService` | `IAuthenticationService` | HMAC-SHA256, issuer whitelist, audience, lifetime |
| `HistoryReducerService` | *(factory)* | Builds `MorganaChatReducer` from config; `null` means "hand the LLM everything" |
| `MorganaChatReducer` | `IChatReducer` | Replaces MEAI's summarizer, which drops every function-call message — so a tool-driven agent's summary reported, accurately for its view, that no tool ran |
| `AdaptingChannelService` | `IChannelService` | Decorator: degrade through `MorganaChannelAdapter`, then dispatch by `deliveryMode` |
| `ChannelMetadataStore` | `IChannelMetadataStore` | Leaf singleton, so concrete transports read per-conversation coordinates without a DI cycle |

## LLM providers and tiers

`MorganaLLM` implements `ILLMService`; four providers (`Anthropic`, `AzureOpenAI`, `OpenAI`,
`Ollama`) selected by `Morgana:LLM:Provider`, all wrapped into `IChatClient`.

**Exactly two dies**, modelled on Intel's E-core/P-core split. `Efficiency` is the default and serves
every framework actor; `Performance` is reserved for agents whose author declares an existential need
for deep reasoning, never a nice-to-have upgrade. **There is no cross-tier fallback**: a single-model
deployment declares only `Efficiency` and any agent requiring `Performance` fails startup until a
second entry exists.

`Tiers` is a JSON **object keyed by tier name**, not an array, so env-var overrides merge per tier.
Each entry carries `Options` — a deliberately narrow, JSON-bindable mirror of `ChatOptions`:
`ModelId` and `MaxOutputTokens` only, see `Records.TierConfiguration` for the census and the reason —
plus its own `MagicDust` pricing.

Two consumption modes: `CompleteWithSystemPromptAsync` (stateless, always on the cheapest configured
tier) and `GetChatClient(tier)` / `GetPricing(tier)` (exact match, no fallback).

## Agent Authoring

1. **Intent** in `agents.json`, Intents array: Name, Description, Label, DefaultValue
2. **Prompt** in `agents.json`, Agents array: ID matching the intent, Target, Instructions,
   Personality, Formatting, ConsultMeFor, Tools
3. **Agent class** extending `MorganaAgent`, with `[HandlesIntent("x")]` **and** `[RequiresLLMTier]`
   (mandatory, validated at startup). The constructor calls `MorganaAgentAdapter.CreateAgent()`
4. **Tool class** (optional) extending `MorganaTool`, with `[ProvidesToolForIntent("x")]`. Method
   names must match the JSON `Name` exactly. Constructor `(ILogger, Func<ToolContext>)`
5. **Or MCP** — `[UsesMCPServer(...)]`, repeatable, tools discovered at runtime
6. **Colleagues** — `[ConsultsAgent("otherintent")]`, once each, validated at startup
7. **Package as a plugin DLL** into `plugins/`

## Tool System

Every agent gets the **base tools** from `morgana.json` (`GetContextVariable`, `SetContextVariable`,
`SetTurnContinuation`, `SetQuickReplies`, `SetRichCard`) plus its domain tools.

A parameter resolving an *input* declares a `Scope`: `context` (looked up before being asked) or
`request` (asked of the user). A parameter carrying a value the model itself authors declares none.
**Neither scope carries a parameter-level template** — the lookup-before-asking rule lives in P0 and
in `ToolDescriptionContextGuidance` and a per-parameter restatement is the only form of it paid on
every round trip. `Scope` still decides whether the *tool* gets its template.

Parameter descriptions reach the model **only** through the JSON schema, via
`AIJsonSchemaCreateOptions.ParameterDescriptionProvider`. MCP tools never pass through
`MorganaToolAdapter`: they arrive carrying their server's schema and Morgana adapts nothing.

**What a tool RETURNS is prose too and it is the one layer with no declared precedence** — it
arrives mid-turn from outside the composed prompt. So a return value states **facts** (what was
written, what this response does and does not carry) and never instructs behaviour. A tool that needs
the agent to behave a certain way is asking for a line of domain `Instructions` or for a global
policy where it holds for every domain. The rule is stated on `MorganaTool` itself, where a plugin
author reads it.

## Prompt Architecture

Two layers in `ComposeAgentInstructionsAsync`:
1. **Framework** (`morgana.json`): Target, Personality, GlobalPolicies, Instructions, Formatting
2. **Domain** (`agents.json`): Target, Personality, Instructions, Formatting

**The fences are load-bearing.** Both layers carry the same four section labels, so an unfenced
composition shows `[TARGET]` twice with nothing saying which is which. The headers and footers are
`const string` fields in `ConfigurationPromptComposerService`, **not** configuration: they are one
design, substitutable only by replacing `IPromptComposerService` itself.

**The domain layer's subordination is total, which is what makes it cheap.** An agent restating a
framework rule is not reinforcement, it is a second voice claiming the same authority. A domain agent
says the least and the most specific possible. Anything it says that a global policy already says
belongs deleted; where two agents need the same sentence, that is a policy gap to be filled **above**,
never a repair below.

A domain prompt carries a fifth authored section, **`ConsultMeFor`, never composed into its own
prompt**: it is the one section whose reader is another agent and it travels out on the A2A card.

### `morgana.json` structure

The `Morgana` prompt's `AdditionalProperties` carry two sibling arrays and **which of the two an
entry is follows from the array it lives in**, never from a field inside it:

- **`GlobalPolicies`** — P0-P8, rendered into every agent's prompt in `Priority` order:
  ContextHandling, QuickReplyDoctrine, TurnContinuation, SessionContinuation, ToolUsage,
  ToolGrounding, MandatoryTextualResponse, RichCardUsage, PeerConsultation. `QuickReplyDoctrine` (P1)
  is the master rule the other quick-reply policies instantiate. `PeerConsultation` (P8) is the
  **only conditionally rendered** one — an agent outside the A2A topology never pays for it — and
  sits last so it names the policies it suspends instead of forward-referencing them.
- **`Injections`** — templates, not rules: prose with a single splice site each, never rendered among
  the policies where they would instruct against nothing. No `Priority`: each is fetched by name.
  `ToolDescriptionContextGuidance` (into a tool's own description), `HeldContextDeclaration` (per
  turn, the variables the session holds — the one entry carrying a *fact* rather than a rule),
  `ColleaguesDeclaration` (closing a peer-capable agent's instructions),
  `PeerConsultationDeclaration` and `PeerConsultationGuardrail` (in front of a colleague's question).

Every injection opens with a **bracketed all-caps label at the head of its first line** — the idiom
the prompt layers already use for `[TARGET]`. A template arrives spliced into somebody else's text,
so where it begins has to be visible without being read. What may share that line is the block's own
**datum** (`((context_parameters))`, `((held_variables))`); prose and anything of any length go under
the label.

The splice sites form a **ladder**: a parameter description is read once the model is already
invoking the tool, a tool description when it weighs the tool, the per-turn injection before any tool
is weighed at all — which is where an agent activated mid-conversation would otherwise fail.

The other framework prompts: **Classifier** (JSON `{intents:[{intent,confidence}]}`, ranked; owns the
`other` complement, which no domain declares), **Guard** (`{compliant, violation}`),
**Presentation**, **ChannelAdapter**.

## Channel Abstraction

```
concrete transports (SignalR, Webhook, ...)   one per deliveryMode
  IChannelServiceFactory                      per-conversation dispatch
    AdaptingChannelService                    registered as IChannelService
ChannelMetadataStore                          leaf singleton, read by both
```

**Handshake**: at start the client announces `ChannelMetadata` — `ChannelCoordinates` (channelName
plus deliveryMode) and `ChannelCapabilities`. The controller rejects a missing or unserved
`deliveryMode`; coordinates are normalized (trim, lowercase) and persisted.

**Adaptation** (`MorganaChannelAdapter.AdaptAsync`): short-circuit if it fits the budget, then an
LLM-guided rewrite, then a Markdig template fallback. Never throws. Streaming is suppressed upstream
when unsupported or when adaptation would be needed.

## Persistence

Per-conversation SQLite at `{StoragePath}/morgana-{conversationId}.db`, schema version in
`PRAGMA user_version` (currently 5), idempotent initialization.

| Table | Purpose |
|---|---|
| `morgana` | Per-agent `AgentSession` BLOBs, AES-256-CBC encrypted |
| `rate_limit_log` | Sliding window |
| `channel_metadata` | The persisted handshake |
| `shared_context` | Cross-agent variables, first-write-wins |
| `dust_budget` · `dust_usage_log` | Lifetime budget, per-charge attribution |

History retrieval decrypts each agent row, applies a user-facing filter, merges chronologically and
extracts quick replies and rich cards from the stored function calls.

## Authentication

JWT, per-issuer trust. A channel self-issues (own `iss`, audience `morgana.ai`, HMAC-SHA256);
`JWTAuthenticationService` peeks `iss`, finds that issuer's entry and validates with **its** key — so
leaking one channel's key does not compromise the others. Unknown issuers are rejected.

**Onboarding a channel**: an `Issuers[]` entry whose `Name` equals the channel's `iss` and whose
`SymmetricKey` matches its signing secret. **Onboarding a partner**: one `Partners[]` entry carrying
name, url, the shared key and one policy per direction. Three switches nest: `Enabled` on the
partner, then `OutboundPolicy.Enabled`, then `InboundPolicy.Enabled`.

## Observability

Spans `morgana.turn`, `morgana.guard`, `morgana.classifier`, `morgana.router`, `morgana.agent`, with
`agent.tools_invoked` carrying tool names and **never** arguments. HTTP Activity context arrives as
an `ActivityLink`. Metric `morgana.dust.consumed` tagged by role, beside MEAI's `gen_ai.usage.*`.
Exporters configured under `Morgana:OpenTelemetry:Exporters`.

## Startup Validation

Nine checks, each fatal, all guarding one failure shape: **a topology that validates cleanly and then
fails or opens, silently.**

1. Every configured intent has an agent and every agent an intent
2. Every agent declares `[RequiresLLMTier]` and that tier is configured
3. Every `[ConsultsAgent]` names a reachable colleague and no two fold to one function name
4. Tools: warn on orphans, error on duplicates for one intent
5. Plugin `agents.json` files merge with no duplicated intent or prompt id and none declares `other`
6. No `Tiers` entry left on its override placeholder, no empty `Tiers` map
7. Every admitted issuer — channel, partner and the ring alike — carries a name and a key of at least 256 bits and no name is admitted twice
8. `ValidateTrustConfiguration`: each `Partners[]` entry names somebody once, does something, is coherent per open direction and carries a key that can sign
9. `ValidatePublishedAddress`: `PublicUrl`, where declared, is absolute, on a bearer-carrying scheme and names one interface

**Where they run matters**: a refusal is a *startup* refusal only because `Program.cs` resolves the
configuration and registry services immediately after building the container. Left lazy, the same
fault reaches a user as a conversation that never answers, on a host that passed its health probe.

## Key Configuration (`appsettings.json`)

| Section | Purpose |
|---|---|
| `Morgana:LLM:Provider` · `:{Provider}` | Provider choice, credentials, the `Tiers` map |
| `Morgana:AgentToAgent` | `Enabled`, `MaxRoundsPerTurn`, `PublicUrl` (declared only where a binding cannot answer for the address), `Partners[]`. Consultation waits are one **ladder** derived from `ActorSystem:TimeoutSeconds`, stated once in `Records.PeerConsultationWaits` |
| `Morgana:ActorSystem` | `TimeoutSeconds`, `EnableGuardrail`, `IntentCollisionThreshold` |
| `Morgana:AdaptiveMessaging` | `EnableStreamingResponse`, `RichFeaturesMinLength` |
| `Morgana:ConversationPersistence` | `StoragePath`, `EncryptionKey` (AES-256, base64, 32 bytes) |
| `Morgana:RateLimiting` · `:DustLimiting` | Limits and their authored error messages. `MagicDust` pricing lives per tier |
| `Morgana:Authentication` | Audience and `Issuers[]` — **channels only** |
| `Morgana:HistoryReducer` · `:OpenTelemetry` · `:Plugins` | Summarization, exporters, plugin scan directories |

Sensitive values carry the `_SECURE_OVERRIDE_` placeholder; real values arrive through User Secrets
or environment variables.

## Build and Run

- **Target**: .NET 10, C# latest (uses C# 14 `extension` blocks)
- **Build**: `dotnet build` from the solution root
- **Run**: `Morgana.Web` (https://localhost:5001) plus `Cauldron` (https://localhost:5002)
- **Harness**: `dotnet test PromptHarness/PromptHarness.csproj` from the repo root — live LLM calls,
  on-demand only. Start from `--filter "FullyQualifiedName~HarnessSmokeTests"` to verify the rig
  before believing any scenario result. See `PromptHarness/CLAUDE.md`
- **Alembic**: `dotnet run` from `Alembic/` (https://localhost:5005); needs no Morgana instance
- **Docker**: `docker compose up` starts Morgana and Cauldron. The TTY channels are profile-gated
  because Spectre.Console must own stdin and stdout — one at a time, via
  `docker compose run --rm --service-ports --use-aliases grimoire`. `--use-aliases` is mandatory: without it
  the webhook callback fails DNS resolution. Alembic is profile-gated too, for the unrelated reason
  that it joins no network: `docker compose --profile authoring up alembic`

## Conventions

- Actor messages are immutable records in `Records.cs`; every cross-party literal is a `Constants` member
- Actors use `Tell`, never `Ask` (streaming) and `Become()` for FSM transitions
- Tool method names match the JSON `Name` exactly
- Prompts resolve by ID: the five framework ids or an intent name
- Rich cards use polymorphic JSON with a `type` discriminator
- Turn continuation is signalled out-of-band by a tool, never by a token inside the response text
- Channel names are normalized to lowercase at ingress
