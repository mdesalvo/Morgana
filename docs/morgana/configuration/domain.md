---
title: "On your side: your domain"
permalink: /morgana/configuration/domain/
eyebrow: Configuration · the developer decides
lede: The domain is the only part of Morgana that is about your business. It is a plugin made of prose and a handful of thin classes. This page lists every piece of it.
---

<div class="pills"><span class="pill you">Required: intents and prompts</span><span class="pill you">Required: one agent class per intent</span><span class="pill">Optional: tools · MCP · colleagues</span></div>

## The shape of a plugin

A domain is a .NET class library that references `Morgana.AI` and `Morgana.Contracts` from NuGet. Its compiled DLL goes into the `plugins/` folder of any Morgana. At startup Morgana loads every assembly there, finds the agents and tools by attribute and **merges** every plugin's `agents.json`.

```text
MyDomain/
├── agents.json            # intents and the prose of every agent (embedded resource)
├── Agents/
│   └── BillingAgent.cs    # [HandlesIntent] [RequiresLLMTier]: a constructor and nothing else
└── Tools/
    └── BillingTool.cs     # [ProvidesToolForIntent]: one public method per tool
```

[Alembic]({{ '/alembic/' | relative_url }}) generates exactly this layout from an interview. Everything below is what it writes for you or what you write by hand.

## 1 · Intents

An intent is a kind of request your users can make. Each has four fields in the `Intents` array of `agents.json`.

| Field | Is |
|---|---|
| `Name` | The identifier that ties the agent, the prose and the classifier together |
| `Description` | What the classifier reads: *requests to view invoices, check what is outstanding...* |
| `Label` | The text on the button Morgana shows for it |
| `DefaultValue` | The sentence that button sends when pressed |

```json
{
  "Name": "billing",
  "Description": "requests to view the list of invoices the nursery issued to the customer, check what is still due or overdue, or review payment history",
  "Label": "🧾 Invoices",
  "DefaultValue": "What can you do with my invoices?"
}
```

> The map is a choice rather than an inventory. Give each intent a clear territory. Two intents whose descriptions overlap will collide in the classifier and the user will be asked to choose.
{: .callout}

`other` is reserved. Morgana owns it and no domain may declare it.

## 2 · Agent prose

One entry in the `Agents` array per intent, with the same name in `ID`. See [prompts]({{ '/morgana/pillars/prompts/' | relative_url }}) for what each section does.

| Section | Write it like this |
|---|---|
| `Target` | What the agent does and where that stops. Short |
| `Personality` | A facet of Morgana, never a new character |
| `Instructions` | Only what is specific to this domain. Anything a global policy already says is deleted |
| `Formatting` | How each tool's output should look. One card shape per tool works well |
| `Territory` | The one sentence other agents read to decide whether a question is this agent's |

Write for the agent's **customers**, never for the domain expert who supplied the answers. Name things the way a customer would, not the way your database does. Do not use capitals for emphasis and do not give the model concrete examples of verbs or data it may take as a closed vocabulary.

## 3 · The agent class

```csharp
[HandlesIntent("billing")]
[RequiresLLMTier(LLMTier.Efficiency)]
[ConsultsAgent("inventory")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(string conversationId, ILanguageModelService llm,
        IPromptResolverService prompts, IConversationPersistenceService persistence,
        ILogger logger, MorganaAgentAdapter adapter, IConfiguration configuration)
        : base(conversationId, llm, prompts, persistence, logger, adapter, configuration) { }
}
```

`[RequiresLLMTier]` is mandatory and checked at startup. **Pick the cheapest tier that holds the prose.** `Performance` is for an agent whose author can argue that deep reasoning is the job itself.

## 4 · Tools

Add a tool class when the agent must read or change something. It extends `MorganaTool`, carries `[ProvidesToolForIntent]` and exposes one public method per tool.

- **Every tool and every parameter needs a `[Description]`.** It is the only thing the model sees of it. Say what the tool *does* there and nowhere else
- **Every parameter declares a scope** when it resolves an input: `Request`, `Context` or `SharedContext`
- **A context parameter is a `string`**
- **A tool never shares a name** with a base tool
- **A return value states facts** and never instructs

The constructor receives `(ILogger, Func<ToolContext>)`.

## 5 · MCP servers

If the capability already exists behind an MCP server, declare it instead of writing a tool class.

```csharp
[HandlesIntent("gardening")]
[RequiresLLMTier(LLMTier.Economy)]
[UsesMCPServer("http://localhost:5006/mcp")]
public class GardeningAgent : MorganaAgent { /* constructor */ }
```

Tools arrive at runtime and enter **last**. One whose name collides with a native tool or another MCP tool is refused. Each server is vetted by the ToolGuard before any agent sees it.

## 6 · Colleagues

`[ConsultsAgent("intent")]` once per colleague. `[ConsultsAgent("intent", "partner")]` for an agent in another installation. Startup checks that each colleague is reachable and that no two fold to one function name. See [agent to agent]({{ '/morgana/operations/a2a/' | relative_url }}).

## 7 · Ship it

Build the project and copy the output into `plugins/`. Several plugins can coexist and their domains are merged. The merge **refuses** a duplicated intent or prompt id, the reserved `other` and the seven framework prompt ids.

In Docker, mount or copy the DLL into the `plugins/` folder of the Morgana image the same way the shipped example is.

## 8 · Prove it

A prompt has no compiler. Before you trust a revision run it through the [PromptHarness]({{ '/prompt-harness/' | relative_url }}). A scenario that regresses is a contradiction in the text and the text is what goes back.

## Beyond the domain: replacing the machinery

Every behavioural concern of the framework is a service behind an interface, with the default implementation registered in `Program.cs`. You can replace any of them by registering your own, without changing framework code.

| Interface | Default | Replace it to |
|---|---|---|
| `IClassifierService` | LLM classifier | Route by rules or by your own model |
| `IGuardRailService` | LLM guards | Plug in a moderation vendor |
| `IConversationPersistenceService` | Encrypted SQLite | Store conversations in your database |
| `IAuthenticationService` | JWT per issuer | Integrate your identity provider |
| `IRateLimitService` | SQLite sliding windows | Share limits across instances |
| `IDustLimitService` | SQLite budget | Bill against your own ledger |
| `IChannelService` | Adapting decorator | Add a transport |
| `IPromptComposerService` | Two-layer composer | Compose prompts your way. The fences are part of this design |
| `IToolRegistryService` | Attribute discovery | Source tools elsewhere |
| `IAgentRegistryService` | Attribute discovery | Source agents elsewhere |

Commands (`ICommand`) are registered only by the host and never by a plugin.
