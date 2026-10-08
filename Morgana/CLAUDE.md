# Morgana — Multi-Agent Multi-Channel Conversational AI Framework

## Before writing any C#

**Load the `code-commentation` skill first, in full, at the start of every coding activity in this
repository** — before the first edit, not after it and again in any later turn that resumes coding.
Unconditional: a new file, a refactor and a single changed statement alike. Having read it earlier in
the session does not waive it. The observed failure mode is exactly that — writing first, then
retrofitting comments once somebody points at an uncommented body.

## Where the rationale lives

This file is the map: what exists, where it is, what breaks if you get it wrong. It is deliberately
not the argument. **The rationale is the whole code**, commented as the `code-commentation` skill
prescribes: a one-line XML doc on what a member achieves, the reason for a branch, an exit or an
ordering on the line of the body that it concerns. Before changing something whose reason is not
obvious, read the body that carries it, not only its doc.

## What is Morgana

A conversational AI framework on **.NET 10**, **Akka.NET** (actor model) and **Microsoft.Agents.AI**.
It orchestrates specialized agents that classify, route and resolve user inquiries, with content
moderation, shared context, tool calling and channel adaptation.

Domain experts model agents **declaratively** — prose in JSON, tools as attributed methods of a thin
C# class — package them as plugin DLLs and the framework handles orchestration, streaming,
persistence, guard rails and observability.

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

**One organization, several agents**: not three unrelated demos but three roles inside one fictional
shop, *The Greenhouse & Nursery*, on **one** SQLite system of record (`Data/Examples.db`, seeded and
rebased onto the current month at deployment). Each agent writes only its own competence, yet the
books stay consistent whichever agent closes a sale: `InventoryAgent`'s order confirmation and
`ContractAgent`'s plan enrolment are the two dispositive actions and both bill through the identical
shared path, `GreenhouseDatabaseHelper.BillCustomerAsync`. `BillingAgent` is read-only — every line
on its books was written by another agent.

The plugin also exists to show two structural things: the agents **consult each other**
(`Billing → Inventory`, `Contract → Billing`, deliberately a chain and not a triangle, so the
refusal of a second hop is exercised too) and `MonkeyAgent` is the one agent whose tools are
acquired at runtime from an MCP server, with an empty context vocabulary.

### Morgana.AI

| Folder | Purpose |
|---|---|
| `Abstractions/` | `MorganaActor`, `MorganaAgent`, `MorganaLLM`, `MorganaTool`, `MorganaWorkflow`, `MorganaHostedAgent` (the `AIAgent` publishing an intent over A2A) |
| `LLMs/` | `Anthropic`, `AzureOpenAI`, `OpenAI`, `Ollama`: the implementations of `MorganaLLM`, one per provider |
| `Actors/` | `ConversationManagerActor`, `ConversationSupervisorActor`, `GuardActor`, `ClassifierActor`, `RouterActor` |
| `Adapters/` | `MorganaAgentAdapter` (agent builder, peer-consultation surface), `MorganaToolAdapter` (tool to `AIFunction`), `MorganaChannelAdapter` (rich to plain degradation) |
| `Attributes/` | `[HandlesIntent]`, `[RequiresLLMTier]`, `[ProvidesToolForIntent]`, `[ProvidesWorkflowForIntent]`, `[RequiresApproval]`, `[ToolParameter]`, `[UsesMCPServer]`, `[ConsultsAgent]` |
| `ChatClients/` | `IChatClient` decorators: `TierDefaultsChatClient`, `DustAccountingChatClient`, `MorganaAnthropicClient`, `ApprovalTurnChatClient` (drops `Reply` from a response asking for approval: that turn is the framework's to close), `TurnClosingChatClient` (closes a turn the model wrote without `Reply`: a forced tool call, structured output where the provider cannot force one), `WorkflowToolsChatClient` (offers the model only the tools that a running workflow's current step allows) |
| `Workflows/` | `WorkflowEngine`: an agent's workflows on Microsoft.Agents.AI.Workflows, rebuilt at every call from the checkpoint kept in the agent's session. `WorkflowLauncherFunction`: the function that starts one workflow, recognised by its type |
| `Tools/` | `ReplyTool`: the framework's own tool, declared on its method as a domain tool is |
| `Interfaces/` · `Services/` | Every service contract and its default implementation |
| `Providers/` | `MorganaAIContextProvider` (context variables plus the shared registry), `MorganaChatHistoryProvider` (stores the whole history, hands the model the current episode only — since the user last left — with earlier tool results marked) |
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
| `Controllers/MorganaController.cs` | REST at `api/morgana`: the conversation |
| `Controllers/CommandController.cs` | REST at `api/morgana`: the command catalogue and its execution. Reaches no actor |
| `Filters/ChannelAuthenticationFilter.cs` · `KnownConversationFilter.cs` · `CommandAdmissionFilter.cs` · `ConversationLimitsFilter.cs` | The REST gates as MVC filters. Their `Order` on each action is the gate order |
| `Hubs/MorganaHub.cs` | SignalR at `/morganaHub` |
| `Filters/PartnerAuthenticationFilter.cs` | The partners' auth gate on the A2A JSON-RPC endpoints: the channels' token validation, narrowed to the partners admitted to each agent, fail-closed. The card endpoint stays open by design |
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
5. Back to **Idle** — the response is forwarded through `IChannelService`, followed by Morgana's own
   closing line (the `AgentExit` message) when an agent signalled completion

### REST API

| Endpoint | Method | Purpose |
|---|---|---|
| `conversation/start` | POST | Validates `ChannelMetadata` (required), settles it on record, then creates the manager actor |
| `conversation/{id}/end` | POST | Stops the supervisor |
| `conversation/{id}/resume` | POST | 404 if unknown; read-only, reports the active agent and the dust level |
| `conversation/{id}/message` | POST | Auth, 404 if unknown, then rate limit, then dust budget, then `UserMessage` |
| `conversation/{id}/history` | GET | `ConversationHistoryResponse` |
| `conversation/{id}/command` | POST | Auth, 404 if unknown, 400 for an unknown name or a missing confirmation, rate limit, dust budget, then runs it; the outcome arrives over the channel |
| `commands` | GET | `CommandCatalogResponse`: every `ICommand` registered in DI. The framework publishes `/compact` |
| `health` | GET | Actor system liveness |

Every endpoint but `health` authenticates through `ChannelAuthenticationFilter` (Bearer JWT, fail-closed).

### Multi-turn and shared context

Every turn closes with one `Reply` call carrying what it awaits from the user (`nothing`,
`typed_answer`, `action_choice`), whether the user is leaving, the actions offered as buttons and the
card — a typed argument whose schema is derived from `Morgana.Contracts`. The buttons that let the user
stay or leave are the framework's (`FrameworkReplies` in `morgana.json`), chosen from that closure by
`TurnReply.ToDelivery`: the agent stays `activeAgent` — later messages skip classification — until the
user leaves.

Tool parameters declared `[ToolParameter(ToolScope.Context, shared: true)]` route their values into a
conversation-scoped `shared_context` registry (first-write-wins, `INSERT OR IGNORE`). Every agent merges it at the start of each turn, so
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
`MorganaAgentAdapter`, `MorganaHostedAgent`, `PartnerAuthenticationFilter` and `Program.cs` section 9.5.

OTel: a `morgana.consultation` span nested under the answering agent's work.

## Service Layer

Extension points follow one pattern: interface in `Interfaces/`, default implementation in
`Services/`, registration in `Program.cs`.

| Service | Interface | Purpose |
|---|---|---|
| `LLMClassifierService` | `IClassifierService` | LLM intent classification; falls back to `"other"` at confidence 0 |
| `LLMGuardRailService` | `IGuardRailService` | LLM policy check. **Fails open** |
| `LLMPresenterService` | `IPresenterService` | Welcome message and quick replies. Never throws |
| `CommandRegistryService` | `ICommandRegistryService` | Publishes every `ICommand` in DI to the channels' palettes; a clashing name or an option declared twice is fatal |
| `CompactHistoryCommand` | `ICommand` | `/compact`: folds the active agent's history on the record, reporting a progress widget. Like every command, it works with its own DI stack and never enters the turn pipeline |
| `ConfigurationPromptResolverService` | `IPromptResolverService` | Two-tier resolution: framework prompts from `morgana.json`, domain from `agents.json`. Throws if one ID is declared in both |
| `ConfigurationPromptComposerService` | `IPromptComposerService` | Assembles everything the model reads: the fenced two-layer prompt, tool descriptions, the per-turn held-context declaration, the colleagues declaration, a colleague's question |
| `ConfigurationAgentDirectoryService` | `IAgentDirectoryService` | Both halves of A2A discovery, plus `ValidateTrustConfiguration` and `ValidatePublishedAddress` |
| `EmbeddedAgentConfigurationService` | `IAgentConfigurationService` | Merges every plugin's `agents.json`. Refuses a duplicated intent or prompt id and the reserved names `other` and `Morgana`. Its refusals are fatal |
| `HandlesIntentAgentRegistryService` | `IAgentRegistryService` | Discovers agents by attribute; bidirectional intent validation; validates `[ConsultsAgent]` |
| `RequiresLLMTierValidationService` | `ILLMTierValidationService` | Every agent must declare a tier the active provider configures |
| `ProvidesToolForIntentRegistryService` | `IToolRegistryService` | Discovers tool classes and projects each into its tool definitions; warns on orphans; errors on duplicates |
| `MCPClientRegistryService` | `IMCPClientRegistryService` | MCP connection pool keyed by URI or `stdio:{command}`. A client the library reports ended is replaced; only discovery is retried, never a tool call |
| `SQLiteConversationPersistenceService` | `IConversationPersistenceService` | Per-conversation SQLite: encrypted session BLOBs, the shared-context registry |
| `SQLiteRateLimitService` | `IRateLimitService` | Sliding window per minute, hour and day. **Fails open** |
| `SQLiteDustLimitService` | `IDustLimitService` | Owns **every** dust question asked anywhere — no caller does the arithmetic itself. Thresholds 70%, 90%, lockout. **Fails open** |
| `SQLitePeerAdmissionService` | `IPeerAdmissionService` | Conversations a partner may open per hour. The one ledger that is not a conversation's (`morgana-peers.db`). **Fails closed** |
| `JWTAuthenticationService` | `IAuthenticationService` | HMAC-SHA256, issuer whitelist, audience, lifetime |
| `HistoryReducerService` | *(factory)* | Builds `MorganaChatReducer` from config; `null` means "hand the LLM everything" |
| `MorganaChatReducer` | `IChatReducer` | Replaces MEAI's summarizer, which drops every function-call message — so a tool-driven agent's summary reported, accurately for its view, that no tool ran |
| `AdaptingChannelService` | `IChannelService` | Decorator: degrade through `MorganaChannelAdapter`, then dispatch by `deliveryMode` |
| `ChannelMetadataStore` | `IChannelMetadataStore` | The one owner of a conversation's channel: normalises the handshake, persists it and refuses a conversation without one. Leaf singleton, so concrete transports read it without a DI cycle |

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
   Personality, Formatting, Territory
3. **Agent class** extending `MorganaAgent`, with `[HandlesIntent("x")]` **and** `[RequiresLLMTier]`
   (mandatory, validated at startup). The constructor calls `MorganaAgentAdapter.CreateAgent()`
4. **Tool class** (optional) extending `MorganaTool`, with `[ProvidesToolForIntent("x")]`: the one
   declaration of the agent's tools. Every public instance method it declares is a tool, carrying
   `[Description]` and `[RequiresApproval]`; every parameter carries `[Description]` and
   `[ToolParameter]`; the method returns a typed record whose properties carry `[Description]` and
   whose nullable `Error`, where present, marks a failed call. Constructor `(ILogger, Func<ToolContext>)`.
   A tool declaring `[RequiresApproval(true)]` runs only once the user has approved that exact call,
   through MEAI's own `ApprovalRequiredAIFunction`. The framework offers the approval buttons; pressing
   an action button that leads to the tool is that approval already
5. **Or MCP** — `[UsesMCPServer(...)]`, repeatable, tools discovered at runtime. They never ask for approval: where one changes something real, the agent's `Instructions` have it ask first
6. **Colleagues** — `[ConsultsAgent("otherintent")]`, once each, validated at startup
7. **Workflows** (optional) — a `MorganaWorkflow` class with `[ProvidesWorkflowForIntent("x")]`, shaped like a MAAI graph: steps are nodes holding their tools, each transition is one `AddEdge` or `AddFailureEdge` and a call with no edge ends the workflow. The class's public properties are the values that an edge carries by name. The agent gets one launcher per workflow, `Start{Name}`, described by the class; the order is the framework's, so no prose restates it. A step names an MCP tool by string and such a workflow is checked when the agent is born, withdrawn from it if it does not hold
8. **Package as a plugin DLL** into `plugins/`

## Tool System

Every agent gets the **base tool** `Reply` (`ReplyTool`) plus its domain tools; an agent declaring workflows also gets one launcher per workflow.

Every parameter declares its scope: `Context` or `Request` (asked of the user). Required is the
signature's alone. Only a `Context` value is shared and a `Context` parameter is a required `string`.
**A `context` parameter is the framework's, not the model's**: `MorganaToolAdapter` drops it from the schema's required list, stores
a value the model passes, reads one it omits from the session and keeps the tool from running when
nobody holds it. No prose tells the model any of this beyond one sentence of `ToolUsage`.

Parameter descriptions reach the model **only** through the JSON schema, via
`AIJsonSchemaCreateOptions.ParameterDescriptionProvider`. No tool is declared in JSON: the framework's
`Reply` is a method like any domain tool and a launcher is projected from its workflow's class. MCP tools never pass
through `MorganaToolAdapter`: they arrive carrying their server's schema and Morgana adapts nothing.
A workflow reads an MCP step's outcome from `isError` and `structuredContent` and carries only the
fields that the server's `outputSchema` declares.

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
composition shows `[TARGET]` twice with nothing saying which is which. **The labels are the
composer's**: `Constants.SectionLabels` puts them in front of each section through
`Records.Prompt.Labeled` wherever a prompt is composed, so no JSON writes them and none can be forgotten. The headers and footers are
`const string` fields in `ConfigurationPromptComposerService`, **not** configuration: they are one
design, substitutable only by replacing `IPromptComposerService` itself.

**The domain layer's subordination is total, which is what makes it cheap.** An agent restating a
framework rule is not reinforcement, it is a second voice claiming the same authority. A domain agent
says the least and the most specific possible. Anything it says that a global policy already says
belongs deleted; where two agents need the same sentence, that is a policy gap to be filled **above**,
never a repair below.

A domain prompt carries a fifth authored section, **`Territory`, never composed into its own
prompt**: it is the one section whose reader is another agent and it travels out on the A2A card.

### `morgana.json` structure

The `Morgana` prompt's `AdditionalProperties` carry sibling arrays and **what an entry is follows
from the array that it lives in**, never from a field inside it:

- **`GlobalPolicies`** — rendered into every agent's prompt in `Priority` order:
  QuickReplyDoctrine, ToolGrounding, MandatoryTextualResponse, RichCardUsage, PeerConsultation.
  `PeerConsultation` (P8) is the
  **only conditionally rendered** one (an agent outside the A2A topology never pays for it) and
  sits last so it names the policies it suspends instead of forward-referencing them.
- **`PromptInjections`** — texts spliced into the instructions or the conversation's messages, one
  splice site each, never rendered among the policies where they would instruct against nothing:
  `ColleaguesDeclaration` (closing a peer-capable agent's instructions), `PeerConsultationDeclaration`
  and `PeerConsultationGuardrail` (in front of a colleague's question), `TurnClosureRequest` (after a
  turn the model wrote without `Reply`).
- **`ToolInjections`** — everything the model reads as part of a tool: what the framework's own tools
  return (`Reply`, the context wrapper, the consultation guards and fallbacks, the workflow results),
  `ReplyNotAccepted`, `EarlierToolResult`, `WorkflowStepReached` and `WorkflowEnded` around a result,
  `ExecutionApprovalGuidance` after an approval tool's description. They hold the voice with no declared
  precedence, so they state facts and never instruct. A domain tool's return is the domain's own.
- **`FrameworkReplies`** — the buttons the framework adds to let the user stay, leave or approve, as data.
- **`Messages`** — on any framework prompt, what Morgana says to the user in her own voice (the agent
  exit, the approval question, the errors, the presenter's fallbacks, the classifier's answers), each
  read by name through `Prompt.GetMessage`.

Both arrays are `{Name, Content}`, fetched by name with their values as `((…))` placeholders. Every
text that wraps somebody else's opens with a **bracketed all-caps label at the head of its first
line** — the idiom the prompt layers already use for `[TARGET]` — so where it begins is visible
without being read; a whole text like "Turn closed." needs none.

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
`deliveryMode`, then `ChannelMetadataStore` settles it before `start` answers: coordinates trimmed
and lowercased, capabilities turned into what Morgana will actually send (a channel too short for
rich features loses them, one whose messages will need adapting loses streaming), then persisted.
Every reader takes that record as is, with no channel rule of its own.

**Surviving a restart**: a conversation is served from its record, never from which endpoint reached
this process first. The channel is read back by the store, the active agent by the supervisor at its
first turn, so a client that only reconnected its transport carries on where it was. `resume` sets
nothing up: it only tells a returning client what to redraw.

**A delivery that fails is repaired by the side that sees it fail.** A SignalR push to an empty group
is lost without a trace on Morgana's side, so the client catches up from the history when it comes
back. A webhook POST that fails is seen by Morgana, which delivers it again for about half a minute.
A reply is dated identically on the push and in the history (`RecordedTimestamp`), which is what lets
a catch-up tell a missed reply from one already shown.

**Adaptation** (`MorganaChannelAdapter.AdaptAsync`): short-circuit if it fits the budget, then an
LLM-guided rewrite, then a Markdig template fallback. Never throws.

## Persistence

Per-conversation SQLite at `{StoragePath}/morgana-{conversationId}.db`, schema version in
`PRAGMA user_version` (currently 6), idempotent initialization.

| Table | Purpose |
|---|---|
| `morgana` | One row per participant, AES-256-CBC encrypted: each agent's `AgentSession`, plus Morgana's own, holding messages alone. `is_dirty` marks a row rewritten behind its agent, which reads it again at its next turn; a turn saved in between keeps the rewrite |
| `rate_limit_log` | Sliding window |
| `channel_metadata` | The persisted handshake |
| `shared_context` | Cross-agent variables, first-write-wins |
| `dust_budget` · `dust_usage_log` | Lifetime budget, per-charge attribution |

**A user's phrase is Morgana's when no agent is active and the active agent's otherwise; an answer
belongs to whoever wrote it.** So the phrase is saved at ingress, before the guard. The copy the
routed agent keeps for its model carries `morgana:context_only`. Chrome enters no row: the fading
banners, the typing indicator. Morgana has a row without being an agent — see
`IConversationPersistenceService`, which alone knows how a row encodes its messages.

History retrieval decrypts each row, applies a user-facing filter, merges chronologically and
reads back the buttons and card each turn recorded as delivered on its user-facing message.

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

Ten checks, each fatal, all guarding one failure shape: **a topology that validates cleanly and then
fails or opens, silently.**

1. Every configured intent has an agent and every agent an intent
2. Every agent declares `[RequiresLLMTier]` and that tier is configured
3. Every `[ConsultsAgent]` names a reachable colleague and no two fold to one function name
4. Tools: warn on orphans, error on duplicates for one intent; every tool method and parameter carries its attributes, every scope combination is one the framework can honour, the record returned is typed with a nullable `error`, no tool is overloaded and no agents.json prompt still declares `Tools` or `Workflows`
5. Plugin `agents.json` files merge with no duplicated intent or prompt id and none declares `other`
   or `Morgana`
6. No `Tiers` entry left on its override placeholder, no empty `Tiers` map
7. Every admitted issuer — channel, partner and the ring alike — carries a name and a key of at least 256 bits and no name is admitted twice
8. `ValidateTrustConfiguration`: each `Partners[]` entry names somebody once, does something, is coherent per open direction and carries a key that can sign
9. `ValidatePublishedAddress`: `PublicUrl`, where declared, is absolute, on a bearer-carrying scheme and names one interface
10. Every workflow class names only tools its agent declares, leads each edge from a tool of its source step, reaches every step from the first and carries only its own public properties, each returned by the edge's tool and taken by a tool of the step it leads to. A tool from an MCP server is checked at the agent's birth instead

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
- A domain tool is its method: no prose about it lives anywhere else
- Prompts resolve by ID: the five framework ids or an intent name
- Rich cards use polymorphic JSON with a `type` discriminator
- A turn is closed out-of-band by `Reply`, never by a token inside the response text
- Channel names are normalized to lowercase at ingress
- **Invariant culture everywhere**: every host sets it as its first statement and library code
  (`Morgana.AI`, `Morgana.Terminal`) still passes `CultureInfo.InvariantCulture` or
  `StringComparison.Ordinal` wherever it formats, parses or compares. A build with
  `-p:AnalysisModeGlobalization=All` reports no CA1304, CA1305, CA1310 or CA1311
