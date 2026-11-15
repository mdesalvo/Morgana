---
title: "2 · Agents"
permalink: /morgana/pillars/agents/
eyebrow: Pillar 2 of 6
lede: Domain specialists that register themselves through attributes and gain capabilities from native tools and MCP servers or from colleagues.
---

## An agent in a dozen lines

An agent is a class that declares what it handles and how capable a model it needs. The constructor does nothing but hand the framework's adapter to the base class. Everything the agent *says* lives in prose, not here.

```csharp
[HandlesIntent("billing")]
[RequiresLLMTier(LLMTier.Efficiency)]
[ConsultsAgent("inventory")]            // a colleague in this installation
[ConsultsAgent("shipping", "acme")]     // a colleague in a partner's installation
public class BillingAgent : MorganaAgent { /* constructor only */ }

[HandlesIntent("gardening")]
[RequiresLLMTier(LLMTier.Economy)]
[UsesMCPServer("http://localhost:5006/mcp")]   // tools found at runtime
public class GardeningAgent : MorganaAgent { /* constructor only */ }
```

| Attribute | Says |
|---|---|
| `[HandlesIntent]` | This agent answers that intent |
| `[RequiresLLMTier]` | The model tier it runs on. Mandatory and checked at startup |
| `[ProvidesToolForIntent]` | On a tool class: these tools belong to that intent |
| `[UsesMCPServer]` | Acquire tools from that MCP server. Repeatable |
| `[ConsultsAgent]` | May ask that colleague a question. Once per colleague |

## Self-registration and fail-fast

At startup Morgana scans the plugin assemblies and finds every agent by attribute. It then checks that intents declared in `agents.json` and agents found in code match **in both directions**. An intent nobody handles or an agent nobody can reach stops the boot. The full list of checks is on the [startup validation page]({{ '/morgana/operations/startup-validation/' | relative_url }}).

The point is to turn a failure that would reach a user as a conversation that never answers into one that stops the deployment.

## Tools

A tool is a public method on a class that extends `MorganaTool`. The attributes are its only declaration.

```csharp
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    public BillingTool(ILogger logger, Func<ToolContext> getToolContext)
        : base(logger, getToolContext) { }

    [Description("The invoices issued to a customer, newest first.")]
    public async Task<string> GetInvoices(
        [Description("The customer's own code, whatever they call it.")]
        [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("How many invoices to return.")]
        [ToolParameter(Records.ToolScope.Request)] int count) { /* ... */ }
}
```

Every agent also holds a few **base tools** ahead of its own: reading and writing context variables, ending the turn, offering quick replies and showing a rich card.

A parameter declares where its value comes from:

| Scope | Meaning |
|---|---|
| `Request` | Ask the user |
| `Context` | Look in this agent's own memory first and ask only if it is missing |
| `SharedContext` | The same but through the registry every agent reads. See [context]({{ '/morgana/pillars/context/' | relative_url }}) |

> **What a tool returns is prose too.** It arrives in the middle of a turn from outside the composed prompt. A return value states facts: what was written and what this response does or does not carry. It never instructs the agent how to behave. If behaviour must change, it belongs in the agent's instructions or in a global policy.
{: .rule}

## MCP: tools from outside

An agent that declares `[UsesMCPServer(...)]` discovers its tools from a [Model Context Protocol](https://modelcontextprotocol.io) server at runtime. To the model they are indistinguishable from native ones. This is how you wrap an existing microservice or a vendor's server without writing a tool class.

Morgana connects over HTTP or stdio, pools the connections and replaces one the library reports as ended. Every MCP server is examined by the **ToolGuard** when it starts and again whenever its tool listing changes. A server that tries to steer an agent is closed to all of them. A tool whose name collides with a native one is refused.

<pre class="mermaid">
sequenceDiagram
  participant A as Agent
  participant P as MCP client pool
  participant TG as ToolGuard
  participant S as MCP server
  Note over A,S: at startup and again whenever the listing changes
  P->>S: list tools
  S-->>P: tool names and schemas
  P->>TG: examine what the server offers
  TG-->>P: admitted or the server is closed to every agent
  Note over A,S: during a turn
  A->>P: call a tool
  P->>S: invoke
  S-->>P: result
  P->>TG: inspect the result
  TG-->>A: result or a quarantine notice
</pre>

In practice MCP *augments* a native agent more often than it replaces one. The Gardening agent in the example is the pure case: it has no tool class at all.

## Colleagues: agent to agent

`[ConsultsAgent("billing")]` adds one more function to the agent's tool list, named `consult_billing`. When the model calls it Morgana asks Billing a question over the [A2A protocol](https://a2a-protocol.org) and hands the answer back. The user sees one question and one answer.

<pre class="mermaid">
graph LR
  U["User"] -- one question --> B["Billing"]
  subgraph here["This Morgana"]
    B -- consult_inventory --> I["Inventory"]
    I -. answer .-> B
  end
  subgraph acme["Partner: acme"]
    S["Shipping"]
  end
  B -- consult_acme_shipping --> S
  S -. answer .-> B
  B -- one answer --> U
</pre>

A colleague in the same installation needs nothing configured. A colleague in **another Morgana** is one entry in `Morgana:AgentToAgent:Partners[]` away. Where a colleague runs is a deployment decision and the prose of an agent never says. The [A2A page]({{ '/morgana/operations/a2a/' | relative_url }}) covers the three shapes.

## Handing the conversation back

An agent stays in service by default so later messages skip classification. It hands the conversation back by calling `SetTurnEnding`. The exception is a turn that shows quick replies or a rich card: those always await an answer. Under the buttons an agent emits, Morgana appends her own escape pair, so the user can always get out and the exit can never loop.
