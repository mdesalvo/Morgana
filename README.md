<table style="border:none;">
  <tr>
    <td width="256">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="256"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A modern and flexible, multi-agent, intent-driven conversational AI framework with MCP protocol support</strong></p>
      <p>
        <img src="https://img.shields.io/badge/.NET-10.0-932BD4?logo=dotnet" alt=".NET 10"/>
        <img src="https://img.shields.io/badge/Akka.NET-932BD4?logo=nuget" alt="Akka.NET"/>
        <img src="https://img.shields.io/badge/Microsoft.Agents.AI-932BD4?logo=nuget" alt="Microsoft.Agents.AI"/>
        <img src="https://img.shields.io/badge/ModelContextProtocol-932BD4?logo=nuget" alt="ModelContextProtocol"/>
      </p>
    </td>
  </tr>
</table>

## Overview

Morgana is a modern **conversational AI framework** designed to handle complex scenarios through a sophisticated **multi-agent intent-driven architecture**. Built on cutting-edge .NET 10 and leveraging the actor model via **Akka.NET**, Morgana orchestrates specialized **AI agents** that collaborate to understand, classify and resolve customer inquiries with precision and context awareness.

The system is powered by **Microsoft.Agents.AI**, enabling seamless integration with Large Language Models (LLMs) while maintaining strict governance through guard rails and policy enforcement.

## Core Philosophy

Traditional chatbot systems often struggle with complexity—they either become monolithic and unmaintainable, or they lack the contextual awareness needed for nuanced customer interactions. Morgana addresses these challenges through:

1. **Agent Specialization**: Each agent has a single, well-defined responsibility with access to specific tools
2. **Actor-Based Concurrency**: Akka.NET provides fault tolerance, message-driven architecture, and natural scalability
3. **Intelligent Routing**: Requests are classified and routed to the most appropriate specialist agent
4. **Policy Enforcement**: A dedicated guard actor ensures all interactions comply with business rules and brand guidelines
5. **Declarative Configuration**: Prompts and agent behaviors are externalized as first-class project artifacts
6. **Automatic Discovery**: Agents self-register through attributes, eliminating manual configuration
7. **P2P Context Synchronization**: Agents share contextual information seamlessly through a message bus architecture
8. **Native Memory Management**: Context and conversation history managed by Microsoft.Agents.AI framework
9. **Personality-Driven Interactions**: Layered personality system with global and agent-specific traits
10. **Plugin Architecture**: Domain agents dynamically loaded at runtime, enabling complete framework/domain decoupling
11. **LLM-Driven Quick Replies**: Dynamic interactive button generation for improved UX in multi-choice scenarios
12. **MCP Integration**: Agents dynamically extend capabilities by consuming external MCP servers—tools become indistinguishable from native implementations

## Architecture

### High-Level Component Flow

```
                          User Request
                              │
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                   ConversationManagerActor                    │
│  (Coordinates, routes, manages stateful conversational flow)  │
└─────────────────────────────┬─────────────────────────────────┘
                              │
                              ▼
┌───────────────────────────────────────────────────────────────┐
│                  ConversationSupervisorActor                  │
│  (Orchestrates the entire multi-turn conversation lifecycle)  │
└──┬───────────┬──────────────┬─────────────────────────────────┘
   │           │              │
   ▼           ▼              ▼
┌───────┐  ┌──────────┐  ┌───────────┐
│ Guard │  │Classifier│  │   Router  │ ← Context Sync Bus
│ Actor │  │  Actor   │  │   Actor   │
└───────┘  └──────────┘  └───────────┘
   │           │               │
   │           │               ▼
   │           │         ┌───────────┐
   │           │         │  Morgana  │
   │           │         │   Agent   │
   │           │         └───────────┘
   │           │               │
   │           │               │
   │           │               ▼
   │           │      ╔════════════════════════════════════════════════╗
   │           │      ║      DYNAMIC DOMAIN AGENTS (Plugin-Loaded)     ║ * Loaded via Plugin
   │           │      ╠════════════════════════════════════════════════╣
   │           │      ║  ┌──────────┐   ┌───────────┐  ┌─────────────┐ ║
   │           │      ║  │ Billing* │   │ Contract* │  │   Monkey*   │ ║
   │           │      ║  │  Agent   │   │   Agent   │  │    Agent    │ ║
   │           │      ║  └──────────┘   └───────────┘  └──────┬──────┘ ║
   │           │      ║       │               │               │        ║
   │           │      ╚═══════┼───────────────┼───────────────┼════════╝
   │           │              │   ┌───────────┘               └───────────────┐
   │           │              │   │                                           │   
   │           │              ▼   ▼                                           │
   │           │         ┌──────────────────────┐                             │
   │           │         │MorganaContextProvider│                             │
   │           │         │  (Context + Thread)  │                             │
   │           │         │                      │                             │
   │           │         │ ┌──────────────────┐ │                             │
   │           │         │ │ SetQuickReplies  │ │                             │
   │           │         │ │   (JSON → QR)    │ │                             │
   │           │         │ └──────────────────┘ │                             │
   │           │         └──────────────────────┘                             │
   │           │              │                                               │
   │           │              │ P2P Context Sync via RouterActor              │
   │           │              │                                               │
   │           │              │                                               │
   └─────┬─────┘              │                                               │
         │                    │                                               │
         ▼                    ▼                                               │  HTTP/SSE
        ┌─────────────────────────────────────────────┐                       │  tools/list
        │           MorganaLLMService                 │                       │  tools/call
        │  (Guardrail, Classification, Tool Calling)  │                       │
        └─────────────────────┬───────────────────────┘                       │
                              │                                               │
                              ▼                                               │
              ┌───────────────────────────────┐                               │
              │             LLM               │                               │
              │ (AzureOpenAI, Anthropic, ...) │                               ▼
              └───────────────────────────────┘                     ┌───────────────────┐  
                                                                    │    MonkeyMCP      │  
                                                                    │     (Azure)       │  
                                                                    │                   │
                                                                    │ • get_monkeys     │
                                                                    │ • get_monkey      │
                                                                    │ • get_journey 🐵  │
                                                                    └───────────────────┘
                                                 MonkeyAgent acquires these tools dynamically
                                                 at runtime via [UsesMCPServers("MonkeyMCP")]
```

### Actors Hierarchy

#### 1. ConversationManagerActor
Coordinates and owns the lifecycle of a single user conversation session.

**Responsibilities:**  
- Creates and supervises the `ConversationSupervisorActor` for the associated conversation
- Acts as the stable entry point for all user messages belonging to one conversation ID
- Forwards user messages to the supervisor and returns structured responses via SignalR
- Ensures that each conversation maintains isolation and state continuity across requests
- Terminates or resets session actors upon explicit user request or system shutdown

**Key Characteristics:**  
- One `ConversationManagerActor` per conversation/session
- Persists for the entire life of the conversation unless explicitly terminated
- Prevents accidental re-creation of supervisors or cross-session contamination

#### 2. ConversationSupervisorActor
The orchestrator that manages the entire conversation lifecycle. It coordinates all child agents and ensures proper message flow.

**Responsibilities:**
- Receives incoming user messages
- Coordinates guard checks before processing
- Routes classification requests
- Delegates to appropriate coordinator agents
- Handles error recovery and timeout scenarios

#### 3. GuardActor
A policy enforcement actor that validates every user message against business rules, brand guidelines and safety policies.

**Capabilities:**
- Profanity and inappropriate content detection
- Spam and phishing attempt identification
- Brand tone compliance verification
- Real-time intervention when violations occur
- LLM-powered contextual policy checks

**Configuration-Driven:**
Guard behavior is defined in `morgana.json` with customizable violation terms and responses, making policy updates deployment-independent.

#### 4. ClassifierActor
An intelligent classifier actor that analyzes user intent and determines the appropriate handling path.

**Intent Recognition:**
The classifier identifies specific intents configured in `agents.json`. **Example** built-in intents are:
- `billing`: Fetch invoices or payment history
- `troubleshooting`: Diagnose connectivity or device issues
- `contract`: Handle service contracts and cancellations
- `other`: General service inquiries
- ...

**Metadata Enrichment:**
Each classification includes confidence scores and contextual metadata that downstream agents can use for decision-making.

#### 5. RouterActor
Coordinator actor that resolves mappings of intents to engage specialized executor agents.
This actor works as a smart router, dynamically resolving the appropriate Morgana agent based on classified intent.

**Context Synchronization Bus:**
RouterActor also serves as the **message bus for P2P context synchronization** between agents. When an agent updates a shared context variable, RouterActor broadcasts the update to all other agents, ensuring seamless information sharing across the system.

#### 6. Morgana Agents (Domain-Specific, Extensible!)
Specialized agents with domain-specific knowledge and tool access. The system includes **example** agents that demonstrate both native tools and MCP integration, but **the architecture is fully extensible** to support any domain-specific intent via autodiscovery.

**BillingAgent** (example - native tools)
- **Tools**: `GetInvoices()`, `GetInvoiceDetails()`, `SetQuickReplies()`
- **Purpose**: Handle all billing inquiries, payment verification, and invoice retrieval
- **Prompt**: Defined in `agents.json` under ID "Billing"
- **Quick Replies**: Dynamically generates invoice selection buttons for improved UX

**TroubleshootingAgent** (example - native tools)
- **Tools**: `RunDiagnostics()`, `GetTroubleshootingGuide()`, `SetQuickReplies()`
- **Purpose**: Diagnose connectivity issues, provide step-by-step troubleshooting
- **Prompt**: Defined in `agents.json` under ID "Troubleshooting"
- **Quick Replies**: Offers interactive guide selection (No Internet, Slow Speed, WiFi Issues)

**ContractAgent** (example - native tools)
- **Tools**: `GetContractDetails()`, `InitiateCancellation()`, `SetQuickReplies()`
- **Purpose**: Handle contract modifications and termination requests
- **Prompt**: Defined in `agents.json` under ID "Contract"
- **Quick Replies**: Yes/No confirmation buttons for critical actions

**MonkeyAgent** (example - **MCP-powered** 🐵)
- **MCP Server**: `MonkeyMCP` (Azure Functions)
- **Acquired Tools**: `get_monkeys`, `get_monkey(name)`, `get_monkey_journey(name)`, `get_all_monkey_journeys`, `get_monkey_business`
- **Purpose**: Educational example demonstrating MCP integration
- **Implementation**: Zero tool code—all capabilities acquired dynamically from MCP server

```csharp
// Morgana.AI.Examples/Agents/MonkeyAgent.cs
namespace Morgana.AI.Examples.Agents;

/// <summary>
/// Educational agent demonstrating MCP (Model Context Protocol) integration.
/// Acquires all tools dynamically from the MonkeyMCP server at runtime.
/// No native tool implementations needed—MCP tools are indistinguishable from native ones!
/// </summary>
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]  // ← This is all you need!
public class MonkeyAgent : MorganaAgent
{
    public MonkeyAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger,
        MorganaAgentAdapter morganaAgentAdapter) : base(conversationId, llmService, promptResolverService, logger)
    {
        (aiAgent, contextProvider) = morganaAgentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }

    // No tool implementations—all acquired from MCP!
    // Tools available: get_monkeys, get_monkey, get_monkey_journey, 
    //                  get_all_monkey_journeys, get_monkey_business
}
```

**User Experience:**
```
User: "Tell me about Curious George"
MonkeyAgent → Calls get_monkey("Curious George") via MCP server
            → Receives monkey details from Azure Functions
            → Synthesizes: "🐵 Curious George is a playful monkey known for his adventures..."

User: "What's his journey?"
MonkeyAgent → Calls get_monkey_journey("Curious George") via MCP
            → Receives journey path with activities and health stats
            → Presents interactive journey narrative
```

**Adding Custom Agents:**
To add a new agent for your domain:
1. Define the intent in `agents.json` (Intents section)
2. Create a prompt configuration for the agent behavior
3. Implement a new class inheriting from `MorganaAgent` → the agent
4. Decorate it with `[HandlesIntent("your_intent")]`
5. **(Optional)** Add `[UsesMCPServers("YourServer")]` for MCP capabilities
6. Implement a new class inheriting from `MorganaTool` → the native tools
7. Decorate it with `[ProvidesToolForIntent("your_intent")]`
8. Exploit `AgentAdapter` to activate the agent as `AIAgent`

The `IAgentRegistryService` and `IAgentConfigurationService` will automatically discover and validate all agents at startup.

## Conversational Memory & Context Management

Morgana leverages **Microsoft.Agents.AI** framework for native conversation history and context management, eliminating the need for manual memory handling.

### AgentThread: Automatic Conversation History

Each `MorganaAgent` maintains conversation continuity through `AgentThread`, which automatically manages multi-turn dialogue:

```csharp
protected AIAgent aiAgent;
protected AgentThread aiAgentThread;
protected MorganaContextProvider contextProvider;

protected async Task ExecuteAgentAsync(Records.AgentRequest req)
{
   // Lazy initialization: thread created once per agent lifecycle
   aiAgentThread ??= aiAgent.GetNewThread();

   // Framework automatically manages conversation history
   AgentRunResponse llmResponse = await aiAgent.RunAsync(req.Content!, aiAgentThread);
}
```

**Key Benefits:**
- **Zero Manual History Management**: No need to manually append user/assistant messages
- **Persistent Context**: Thread maintains full conversation context across multiple turns
- **Framework-Native**: Leverages Microsoft.Agents.AI built-in memory capabilities
- **Lazy Initialization**: Thread created on-demand, reducing resource overhead

### MorganaContextProvider: Stateful Context Management

`MorganaContextProvider` implements `AIContextProvider` to manage agent-specific state and enable P2P context synchronization:

```csharp
public class MorganaContextProvider : AIContextProvider
{
   // Source of truth for agent context variables
   public Dictionary<string, object> AgentContext { get; private set; }

   // Shared variable tracking for P2P sync
   private readonly HashSet<string> sharedVariableNames;

   // Callback for broadcasting shared updates
   public Action<string, object>? OnSharedContextUpdate { get; set; }
}
```

**Core Responsibilities:**
1. **Variable Storage**: Maintains `Dictionary<string, object>` for all context variables
2. **Shared Variable Detection**: Tracks which variables should be broadcast to other agents
3. **Merge Strategy**: Implements first-write-wins for incoming context updates
4. **Serialization Support**: Enables persistence with `AgentThread` for future enhancements
5. **Temporary Variable Management**: `DropVariable()` for explicit cleanup of ephemeral data

### Integration with Tools

Tools interact with context through `MorganaContextProvider`:

```csharp
public class MorganaTool
{
  protected readonly Func<MorganaContextProvider> getContextProvider;

  public Task<object> GetContextVariable(string variableName)
  {
    MorganaContextProvider provider = getContextProvider();
    object? value = provider.GetVariable(variableName);
    // Returns value or "not available" message
  }

  public Task<object> SetContextVariable(string variableName, string variableValue)
  {
    MorganaContextProvider provider = getContextProvider();
    provider.SetVariable(variableName, variableValue);

    // If variable is shared, provider automatically triggers broadcast
  }
}
```

### Interaction Token: `#INT#`

Agents use the special token `#INT#` to signal when additional user input is required:

```csharp
bool requiresMoreInput = llmResponseText.Contains("#INT#");
string cleanText = llmResponseText.Replace("#INT#", "").Trim();

return new AgentResponse(cleanText, isCompleted: !requiresMoreInput);
```

- **Present**: Conversation continues, agent awaits more information
- **Absent**: Conversation completed, user may close or start new request

This enables natural back-and-forth dialogues within a single intent execution.

## Context Synchronization System

Morgana implements a sophisticated **P2P context synchronization** mechanism powered by `MorganaContextProvider`, allowing agents to share contextual information seamlessly and eliminating redundant user interactions.

### The Problem

Without context synchronization, each specialized agent maintains its own isolated context. This leads to frustrating user experiences:

```
User → BillingAgent: "Show me my invoices"
BillingAgent: "What's your customer ID?"
User: "USER99"
BillingAgent: [Shows invoices]

User → ContractAgent: "Show me my contract"
ContractAgent: "What's your customer ID?" ← User already provided this!
User: "USER99" ← Frustrating repetition
```

### The Solution: Shared Context with P2P Broadcast

Morgana's context system allows agents to declare which parameters should be **shared** across the system. When one agent collects this information, it's automatically broadcast to all other agents through `MorganaContextProvider`.

### Architecture

#### 1. Declarative Configuration
Parameters are marked as `Shared: true` in `agents.json`:

```json
{
   "Name": "userId",
   "Description": "Customer identifier",
   "Required": true,
   "Scope": "context",
   "Shared": true
}
```

- **`Scope: "context"`**: Parameter is stored in agent context (checked via `GetContextVariable`)
- **`Scope: "request"`**: Parameter must be provided by user in current message
- **`Shared: true`**: Parameter is broadcast to all agents when set
- **`Shared: false`**: Parameter remains private to the agent

#### 2. MorganaContextProvider as State Manager

`MorganaContextProvider` centralizes context management and enables P2P synchronization:

**Variable Storage:**
```csharp
public Dictionary<string, object> AgentContext { get; private set; }
```

**Shared Variable Detection:**
```csharp
private readonly HashSet<string> sharedVariableNames;

public void SetVariable(string variableName, object variableValue)
{
  AgentContext[variableName] = variableValue;

  if (sharedVariableNames.Contains(variableName))
  {
    // Trigger broadcast callback
    OnSharedContextUpdate?.Invoke(variableName, variableValue);
  }
}
```

**Context Merging:**
```csharp
public void MergeSharedContext(Dictionary<string, object> sharedContext)
{
  foreach (var kvp in sharedContext)
  {
    // First-write-wins: accept only if not already present
    if (!AgentContext.ContainsKey(kvp.Key))
    {
      AgentContext[kvp.Key] = kvp.Value;
    }
  }
}
```

#### 3. RouterActor as Message Bus

RouterActor serves dual purposes:
1. **Intent Routing**: Routes requests to appropriate agents
2. **Context Bus**: Broadcasts shared context updates to all agents

When an agent updates a shared variable through `MorganaContextProvider`:

```
BillingAgent → SetContextVariable("userId", "USER99")
   ↓
MorganaContextProvider detects userId is Shared
   ↓
Invokes callback → OnSharedContextUpdate
   ↓
MorganaAgent → ActorSelection("/user/router-{conversationId}")
   ↓
RouterActor → Tell(ContractAgent, ReceiveContextUpdate)
RouterActor → Tell(TroubleshootingAgent, ReceiveContextUpdate)
   ↓
Agents → MorganaContextProvider.MergeSharedContext()
```

#### 4. Agent-Level Integration

Each `MorganaAgent` registers the broadcast callback during initialization:

```csharp
// In AgentAdapter.CreateContextProvider()
MorganaContextProvider provider = new MorganaContextProvider(
  contextProviderLogger, sharedVariables);

if (sharedContextCallback != null)
  provider.OnSharedContextUpdate = sharedContextCallback;

// In MorganaAgent
protected void OnSharedContextUpdate(string key, object value)
{
  string intent = GetType().GetCustomAttribute<HandlesIntentAttribute>()?.Intent;

  Context.ActorSelection($"/user/router-{conversationId}")
         .Tell(new BroadcastContextUpdate(intent, new Dictionary<string, object> { [key] = value }));
}

private void HandleContextUpdate(ReceiveContextUpdate msg)
{
  contextProvider.MergeSharedContext(msg.UpdatedValues);
}
```

### Implementation Flow

```
1. User → BillingAgent: "Show invoices"
2. BillingAgent → GetContextVariable("userId") → MISS
3. BillingAgent → User: "What's your customer ID?"
4. User: "USER99"
5. BillingAgent → SetContextVariable("userId", "USER99")
6. MorganaContextProvider:
  - Stores userId in AgentContext
  - Detects userId is Shared
  - Invokes OnSharedContextUpdate callback
7. BillingAgent → ActorSelection("/user/router-{conversationId}")
8. RouterActor → Broadcast to all agents except sender:
  - Tell(ContractAgent, ReceiveContextUpdate("userId", "USER99"))
  - Tell(TroubleshootingAgent, ReceiveContextUpdate("userId", "USER99"))
9. ContractAgent & TroubleshootingAgent:
  - Receive ReceiveContextUpdate message
  - Call MorganaContextProvider.MergeSharedContext()
  - Store userId if not already present (first-write-wins)
10. User → ContractAgent: "Show my contract"
11. ContractAgent → GetContextVariable("userId") → HIT! "USER99"
12. ContractAgent → Uses USER99 without asking ✅
```

### Key Components

**MorganaContextProvider**
- Maintains `Dictionary<string, object> AgentContext` as source of truth
- Tracks `HashSet<string> sharedVariableNames` for broadcast detection
- Invokes `OnSharedContextUpdate` callback when shared variables are set
- Implements `MergeSharedContext()` with first-write-wins strategy
- Supports serialization/deserialization for future persistence needs

**MorganaTool**
- Receives `Func<MorganaContextProvider>` lazy accessor
- `GetContextVariable` delegates to provider's `GetVariable()`
- `SetContextVariable` delegates to provider's `SetVariable()`
- Provider automatically handles broadcast for shared variables

**MorganaAgent**
- Implements `OnSharedContextUpdate` callback to broadcast via ActorSelection
- Implements `HandleContextUpdate` handler to receive and merge updates
- Registers callback with `MorganaContextProvider` during initialization

**AgentAdapter**
- Extracts shared variables from `ToolDefinition` parameters
- Creates `MorganaContextProvider` with shared variable names
- Registers `OnSharedContextUpdate` callback during agent creation

**RouterActor**
- Receives `BroadcastContextUpdate` messages from agents
- Broadcasts `ReceiveContextUpdate` to all agents except sender
- Logs broadcast activity for observability

### Benefits

1. **Seamless UX**: Users provide information once, all agents benefit
2. **P2P Architecture**: No central bottleneck, agents communicate via message bus
3. **Declarative**: Shared vs private is configured in JSON, not code
4. **Framework-Native**: Built on `AIContextProvider` interface
5. **Fault Tolerant**: Fire-and-forget messages, agents don't block on sync
6. **Scalable**: Adding new agents or shared parameters requires only configuration
7. **Flexible**: Agents can have both shared and private context variables
8. **First-Write-Wins**: Prevents conflicts through intelligent merge strategy

### Example Configuration

```json
{
  "ID": "Billing",
  "Tools": [
    {
      "Name": "GetInvoices",
      "Description": "Gets the invoices of the user",
      "Parameters": [
        {
          "Name": "userId",
          "Scope": "context",
          "Shared": true
        },
        {
          "Name": "count",
          "Scope": "request"
        }
      ]
    }
  ]
}
```

In this example:
- `userId` will be shared across all agents when collected
- `count` is request-specific and never shared

## Quick Replies System

Morgana implements a **LLM-driven Quick Replies** system that enables agents to dynamically generate interactive button options for users, significantly improving UX for multi-choice scenarios and guided conversations.

### The Problem

Traditional conversational flows force users to type responses even when choices are predictable:

```
Agent: "Here are your 4 recent invoices: INV-001, INV-002, INV-003, INV-004. Which one would you like to see?"
User: "Show me INV-002"  ← User has to type the invoice number
```

This creates friction and potential for input errors, especially on mobile devices.

### The Solution: Dynamic Interactive Buttons

The Quick Replies system allows the **LLM to decide** when to offer interactive buttons based on context:

```
Agent: "I found 4 recent invoices for you. Select one below to view details."
Buttons: [📄 INV-001 €130] [📄 INV-002 €150] [📄 INV-003 €125] [📄 INV-004 €100]
User: [clicks button]
Agent: [shows invoice details immediately]
```

### Architecture

#### 1. SetQuickReplies System Tool

The LLM has access to a **system tool** that enables dynamic button generation:

**Tool Definition** (`morgana_en.json`):
```json
{
  "Name": "SetQuickReplies",
  "Description": "SYSTEM TOOL to create interactive button options for the user...",
  "Parameters": [
    {
      "Name": "quickReplies",
      "Description": "JSON string containing quick reply button definitions",
      "Required": true,
      "Scope": "context",
      "Shared": false
    }
  ]
}
```

**LLM Invocation:**
The LLM calls this tool during response generation:

```json
SetQuickReplies([
  {
    "id": "invoice-001",
    "label": "📄 Invoice INV-2025-001 (€130)",
    "value": "Show details for invoice INV-2025-001"
  },
  {
    "id": "invoice-002",
    "label": "📄 Invoice INV-2025-002 (€150)",
    "value": "Show details for invoice INV-2025-002"
  }
])
```

#### 2. Storage in MorganaContextProvider

Quick replies are stored as **temporary context** using the special key `__pending_quick_replies`:

```csharp
// MorganaTool.SetQuickReplies()
public Task<object> SetQuickReplies(string quickReplies)
{
    // Validate and parse JSON
    var parsedQuickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(quickRepliesJSON);
    
    // Store as temporary context (private, not shared)
    contextProvider.SetVariable("__pending_quick_replies", quickReplies);
    
    return Task.FromResult<object>(
        "Quick reply buttons set successfully. The user will see N interactive options.");
}
```

**Key Characteristics:**
- **Private**: `Shared: false` - buttons are agent-specific, not broadcast to other agents
- **Temporary**: Cleared immediately after retrieval via `DropVariable()`
- **JSON Storage**: Stored as string, deserialized on retrieval for compatibility

#### 3. Agent Retrieval Pipeline

After LLM execution, the agent retrieves and propagates quick replies:

```csharp
// MorganaAgent.ExecuteAgentAsync()
protected async Task ExecuteAgentAsync(Records.AgentRequest req)
{
    // Execute LLM (may call SetQuickReplies tool)
    AgentRunResponse llmResponse = await aiAgent.RunAsync(req.Content!, aiAgentThread);
    
    // Retrieve quick replies set by LLM during execution
    List<Records.QuickReply>? quickReplies = GetQuickRepliesFromContext();
    
    // Include in response
    return new AgentResponse(cleanText, isCompleted, quickReplies);
}

// MorganaAgent.GetQuickRepliesFromContext()
protected virtual List<Records.QuickReply>? GetQuickRepliesFromContext()
{
    // Retrieve JSON from context
    var json = contextProvider.GetVariable("__pending_quick_replies") as string;
    
    if (!string.IsNullOrEmpty(json))
    {
        // Deserialize
        var quickReplies = JsonSerializer.Deserialize<List<Records.QuickReply>>(json);
        
        // Clear temporary variable immediately
        contextProvider.DropVariable("__pending_quick_replies");
        
        return quickReplies;
    }
    
    return null;
}
```

#### 4. Actor Pipeline Propagation

Quick replies flow through the actor hierarchy to the UI:

```
MorganaAgent → AgentResponse(text, isCompleted, quickReplies)
    ↓
RouterActor → ActiveAgentResponse(text, isCompleted, agentRef, quickReplies)
    ↓
ConversationSupervisorActor → ConversationResponse(..., quickReplies)
    ↓
ConversationManagerActor → SignalR.SendStructuredMessageAsync(..., quickReplies)
    ↓
Cauldron UI → Renders interactive buttons
```

### Agent Completion Logic

When an agent offers quick replies, it **must remain active** to handle button clicks:

```csharp
bool hasInteractiveToken = llmResponseText.Contains("#INT#");
bool endsWithQuestion = llmResponseText.TrimEnd().EndsWith("?");
bool hasQuickReplies = quickReplies != null && quickReplies.Any();

// Agent is NOT completed if it offers quick replies
bool isCompleted = !hasInteractiveToken && !endsWithQuestion && !hasQuickReplies;
```

**Why?** Without this logic:
1. Agent completes → `activeAgent = null`
2. User clicks button → message sent
3. No active agent → goes to Classifier
4. Classifier: "Show details for invoice INV-001" → **"other" intent** ❌

**With this logic:**
1. Agent offers buttons → `isCompleted = false`
2. Agent remains active
3. User clicks button → **follow-up flow** to active agent ✅
4. Agent handles request directly

### TEXT vs BUTTONS Pattern

To prevent duplication, the LLM is guided to follow the **"TEXT introduces, BUTTONS execute"** pattern:

**❌ BAD - Redundant:**
```
TEXT: "Your invoices are: INV-001 (€130), INV-002 (€150), INV-003 (€125)"
BUTTONS: [INV-001] [INV-002] [INV-003]
```

**✅ GOOD - Complementary:**
```
TEXT: "I found 3 recent invoices. Select one below to view details."
BUTTONS: [📄 INV-001 €130] [📄 INV-002 €150] [📄 INV-003 €125]
```

**Prompt Guidance** (`morgana.json`):
```
CRITICAL: When using quick replies, your TEXT response should be a brief contextual 
introduction WITHOUT listing the options themselves - the buttons will show the options 
visually. Example: Instead of '1. Invoice A, 2. Invoice B, 3. Invoice C' write 
'I found 3 recent invoices. Select one below to view details.'
```

### Use Cases

**Invoice Selection (BillingAgent):**
```
User: "Show me my invoices"
Agent: "I discovered 4 invoices for you. Select one to view the enchanted details."
Buttons: [📄 INV-001 €130] [📄 INV-002 €150] [📄 INV-003 €125] [📄 INV-004 €100]
```

**Troubleshooting Guides (TroubleshootingAgent):**
```
User: "My internet is not working"
Agent: "I have 3 guides that can help. Choose the one matching your issue."
Buttons: [🔴 No Internet] [🐌 Slow Speed] [📡 WiFi Issues]
```

**Contract Termination Confirmation (ContractAgent):**
```
User: "I want to cancel my contract"
Agent: "This will trigger termination with a €300 fee. Are you certain?"
Buttons: [✅ Yes, Proceed] [❌ No, Cancel]
```

### Configuration

**Tool Definition** (`morgana.json`):
```json
{
  "Name": "SetQuickReplies",
  "Parameters": [
    {
      "Name": "quickReplies",
      "Scope": "context",
      "Shared": false
    }
  ]
}
```

**Agent Instructions** (`agents.json`):
```json
{
  "ID": "Billing",
  "Instructions": "... QUICK REPLY USAGE: When showing a list of invoices, 
  call SetQuickReplies to let users quickly select an invoice. IMPORTANT: Your 
  TEXT response should be brief and contextual WITHOUT listing all invoice details. 
  Format: [{\"id\": \"INV-001\", \"label\": \"📄 Invoice INV-001 (€130)\", 
  \"value\": \"Show details for invoice INV-001\"}]. Remember: TEXT introduces, 
  BUTTONS execute."
}
```

### QuickReply Record Definition

```csharp
namespace Morgana.AI.Records;

public record QuickReply(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value);
```

**Fields:**
- **`id`**: Unique identifier (e.g., `"invoice-001"`)
- **`label`**: Display text with emoji (e.g., `"📄 Invoice INV-001 (€130)"`)
- **`value`**: Message sent when clicked (e.g., `"Show details for invoice INV-001"`)

### Benefits

1. **LLM-Driven**: Agent decides dynamically when buttons are appropriate
2. **Zero Configuration**: No hardcoded button definitions in code
3. **Context-Aware**: Buttons adapt to conversation state and available data
4. **Mobile-Friendly**: Reduces typing on mobile devices
5. **Error Prevention**: Eliminates typos in invoice numbers, option names, etc.
6. **Guided UX**: Users see available options explicitly, reducing confusion
7. **Agent Continuity**: Button clicks handled via follow-up flow, maintaining context
8. **Temporary Storage**: Buttons don't pollute persistent context, cleared after use

## Personality System

Morgana implements a **layered personality architecture** that creates consistent yet contextually appropriate conversational experiences. The system combines a global personality with agent-specific traits to ensure brand coherence while allowing specialized behavior.

### Architecture

**Two-Tier Personality Model:**

1. **Global Personality (Morgana)**: Base character and tone applied to all interactions
2. **Agent-Specific Personality**: Specialized traits that complement the global personality

### Configuration

**Global Personality** (`morgana.json` - Morgana prompt):
```json
{
  "ID": "Morgana",
  "Content": "You are a modern and effective digital assistant...",
  "Instructions": "Ongoing conversation with the user. Stay consistent...",
  "Personality": "Your name is Morgana: you are a 'good witch' who uses your knowledge of magic to formulate potions and spells to help the user..."
}
```

**Agent-Specific Personality** (`agents.json` - Billing prompt):
```json
{
  "ID": "Billing",
  "Content": "You're familiar with the book of potions and spells called 'Billing and Payments'...",
  "Instructions": "NEVER invent procedures or information...",
  "Personality": "Be consistent with your character: let's say that in this specific role, you could be a fairly traditional witch who favors concreteness and pragmatism..."
}
```

### Instruction Composition

The `AgentAdapter` composes instructions in a specific order to ensure proper personality layering:

```csharp
private string ComposeAgentInstructions(Prompt agentPrompt)
{
   var sb = new StringBuilder();

   // 1. Global role and capabilities
   sb.AppendLine(morganaPrompt.Content);

   // 2. Global personality (Morgana's character)
   if (!string.IsNullOrEmpty(morganaPrompt.Personality))
   {
     sb.AppendLine(morganaPrompt.Personality);
     sb.AppendLine();
   }

   // 3. Global policies (context handling, interaction rules)
   sb.AppendLine(formattedPolicies);
   sb.AppendLine();

   // 4. Global operational instructions
   sb.AppendLine(morganaPrompt.Instructions);
   sb.AppendLine();

   // 5. Agent-specific role and domain
   sb.AppendLine(agentPrompt.Content);

   // 6. Agent-specific personality (specialized traits)
   if (!string.IsNullOrEmpty(agentPrompt.Personality))
   {
     sb.AppendLine(agentPrompt.Personality);
     sb.AppendLine();
   }

   // 7. Agent-specific instructions
   sb.Append(agentPrompt.Instructions);

   return sb.ToString();
}
```

### Personality Examples

**BillingAgent**:
- **Global**: Helpful, slightly mysterious, uses magical metaphors
- **Agent-Specific**: Traditional, pragmatic, concrete (because nobody likes dealing with bills)

**ContractAgent**:
- **Global**: Helpful, slightly mysterious, uses magical metaphors
- **Agent-Specific**: Patient, professional, accustomed to cancellation requests

**TroubleshootingAgent**:
- **Global**: Helpful, slightly mysterious, uses magical metaphors
- **Agent-Specific**: Technical but graceful, oriented to results, empathetic with frustrated users

### Benefits

1. **Brand Consistency**: All interactions maintain Morgana's core character
2. **Contextual Adaptation**: Each agent adapts personality to domain appropriateness
3. **Natural Handoffs**: Personality continuity across agent transitions
4. **Declarative Configuration**: No code changes needed to adjust personalities
5. **Subordination Principle**: Agent personalities complement, never contradict, global personality
6. **Domain Empathy**: Agents can express understanding appropriate to context (e.g., billing frustration, technical issues)

### Design Philosophy

The personality system follows a **subordination principle**: agent-specific personalities enhance and contextualize the global personality without conflicting. This creates:

- **Vertical Consistency**: Same core character across all interactions
- **Horizontal Variation**: Appropriate tone adjustments per domain
- **Seamless Experience**: Users interact with "Morgana" who happens to be specialized in different areas

The system avoids creating disconnected personas—users always feel they're talking to the same assistant, just with domain-appropriate expertise and empathy.

## Prompt Management System

Morgana treats prompts as **first-class project artifacts**, not hardcoded strings. The `IPromptResolverService` provides a flexible, maintainable approach to prompt engineering.

### Key Components

**IPromptResolverService**
- Centralizes prompt retrieval and resolution
- Supports structured prompt definitions with metadata
- Enables versioning and A/B testing of prompts
- Validates prompt consistency across agents

**ConfigurationPromptResolverService**
- Loads prompts from embedded `morgana.json` and `agents.json` resources
- Parses structured prompt definitions including:
- System instructions
- Personality definitions
- Global policies
- Tool definitions
- Error messages
- Additional properties
- Language and versioning metadata

**Prompt Structure**
```json
{
  "ID": "billing",
  "Type": "INTENT",
  "SubType": "AGENT",
  "Content": "Agent role and domain description",
  "Personality": "Agent-specific character traits",
  "Instructions": "Behavioral rules and operational guidelines",
  "AdditionalProperties": [
    {
      "GlobalPolicies": [...],
      "ErrorAnswers": [...],
      "Tools": [...]
    }
  ]
}
```

### Global Policies

Global policies define system-wide behavioral rules that apply to all agents:

```json
{
  "GlobalPolicies": [
    {
      "Name": "ContextHandling",
      "Description": "CRITICAL CONTEXT RULE - Before asking...",
      "Type": "Critical",
      "Priority": 0
    },
    {
      "Name": "InteractiveToken",
      "Description": "OPERATIONAL RULE INTERACTION TOKEN '#INT#' - You operate....",
      "Type": "Operational",
      "Priority": 0
    },
    {
      "Name": "ToolParameterContextGuidance",
      "Description": "OPERATION RULE ON AGENT TOOLS CONTEXT PARAMETERS - Operates...",
      "Type": "Operational",
      "Priority": 1
    },
    {
      "Name": "ToolParameterRequestGuidance",
      "Description": "OPERATIONAL RULE ON DIRECT REQUEST PARAMETERS OF AGENT TOOLS - Operate...",
      "Type": "Operational",
      "Priority": 2
    },
    {
      "Name": "SetQuickRepliesGuidance",
      "Description": "SYSTEM TOOL FOR QUICK REPLIES - When you present multiple options...",
      "Type": "Operational",
      "Priority": 3
    }
  ]
}
```

**Policy Types:**
- **Critical**: Core rules that must never be violated
- **Operational**: Procedural guidelines for consistent behavior

**Policy Formatting:**
The `AgentAdapter` formats policies preserving order and structure:

```csharp
//Order the policies by type (Critical, Operational) then by priority (the lower is the most important)
foreach (GlobalPolicy policy in policies.OrderBy(p => p.Type).ThenBy(p => p.Priority))
{
  var sb = new StringBuilder();
  foreach (var policy in policies)
  {
    sb.AppendLine($"{policy.Name}: {policy.Description}");
  }
  return sb.ToString().TrimEnd();
}
```

### Error Messages

Centralized error message management for consistent user experience:

```json
{
  "ErrorAnswers": [
    {
      "Name": "GenericError",
      "Content": "Sorry, an error occurred while servicing your request."
    },
    {
      "Name": "LLMServiceError",
      "Content": "Sorry, the AI ​​service generated an unexpected error: ((llm_error))"
    }
  ]
}
```

### Tool Parameter Guidance

Parameter-level guidance is dynamically applied based on `Scope`:

```json
{
  "Parameters": [
    {
      "Name": "userId",
      "Scope": "context",
      "Shared": true
    },
    {
      "Name": "count",
      "Scope": "request"
    }
  ]
}
```

The `ToolAdapter` enriches descriptions with appropriate guidance:
- **`Scope: "context"`**: Adds `ToolParameterContextGuidance` policy
- **`Scope: "request"`**: Adds `ToolParameterRequestGuidance` policy

### Benefits
- **Separation of Concerns**: Prompt engineering decoupled from application logic
- **Rapid Iteration**: Update agent behavior without recompiling
- **Consistency**: Single source of truth for agent instructions and policies
- **Auditability**: Version-controlled prompt evolution
- **Localization Ready**: Multi-language support built-in
- **Policy Centralization**: Global rules applied uniformly across all agents
- **Error Consistency**: Standardized error messaging across the system

## Agent Registration & Discovery

Morgana uses **declarative intent mapping** through the `[HandlesIntent]` attribute, enabling automatic agent discovery and validation.

### How It Works

**1. Declarative Intent Handling**
```csharp
[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
   // Agent implementation
}
```

**2. Automatic Discovery**
The `AgentRegistryService` scans the assembly at startup to find all classes:
- Inheriting from `MorganaAgent`
- Decorated with `[HandlesIntent]`

**3. Bidirectional Validation**
The system enforces consistency between:
- Intents declared in agent classes
- Intents configured in the Classifier prompt

At startup, it verifies:
- Every classifier intent has a registered agent
- Every registered agent has a corresponding classifier intent

**4. Runtime Resolution**
```csharp
Type? agentType = agentRegistryService.ResolveAgentFromIntent("billing");
// Returns typeof(BillingAgent)
```

### Validation Guarantees
The system throws explicit exceptions if:
- A classifier intent has no corresponding agent
- An agent declares an intent not configured for classification
- Tool definitions mismatch between prompts and implementations

This **fail-fast approach** ensures configuration errors are caught at startup, not during customer interactions.

## Plugin System

The system dynamically loads domain assemblies configured in `appsettings.json` under `Plugins:Assemblies`. At bootstrap, `PluginLoader` validates that each assembly contains at least one class extending `MorganaAgent`, otherwise it's skipped with a warning. This enables complete decoupling between framework (Morgana.AI) and application domains (e.g., Morgana.AI.Examples), while maintaining automatic discovery of agents and tools via reflection.

### Configuration

**appsettings.json:**
```json
{
  "Plugins": {
    "Assemblies": [
      "Morgana.AI.Examples.dll" // Exposes 3 demo agents
    ]
  }
}
```

### Project Setup

```xml
<ItemGroup>
  <ProjectReference Include="..\Morgana.AI.Examples\Morgana.AI.Examples.csproj" />
</ItemGroup>
```

### Bootstrap Process

1. **PluginLoader** reads `Plugins:Assemblies` from configuration
2. Each assembly is loaded via `Assembly.Load()`
3. Validation ensures assembly contains at least one `MorganaAgent` subclass
4. Valid assemblies are loaded; invalid ones are skipped with warnings
5. Agent and tool discovery proceeds via reflection across all loaded assemblies

### Benefits

- **Zero Compile-Time Coupling**: Framework has no direct dependencies on domain code
- **Runtime Extensibility**: Add new domains by dropping assemblies and updating configuration
- **Validation at Startup**: Invalid plugins are caught early with clear diagnostics
- **Multi-Domain Support**: Load multiple domain assemblies simultaneously
- **Clean Separation**: Framework (Morgana.AI) and domains (*.Examples, *.YourDomain) remain independent

## MCP Integration: Extending Agent Capabilities Dynamically

Morgana supports **Model Context Protocol (MCP)** servers, enabling agents to dynamically acquire capabilities from external tools and services without code changes. Each agent can declare its use of MCP servers, and at runtime, Morgana automatically discovers all exposed tools and integrates them as native methods—**indistinguishable from built-in tools**.

### The MCP Advantage

**Traditional Approach:**
```csharp
// Adding a new capability requires code changes
public async Task<string> GetMonkeyInfo(string name) { /* ... */ }
```

**MCP Approach:**
```csharp
// Agent declares MCP server usage via attribute
[UsesMCPServers("MonkeyMCP")]
public class MonkeyAgent : MorganaAgent { }

// Tools are automatically discovered and integrated at runtime!
// get_monkey, get_monkey_journey, get_monkeys, etc. → Native Morgana tools
```

### How It Works

**1. Declarative Server Usage**
```csharp
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]  // Declare dependency on MCP server
public class MonkeyAgent : MorganaAgent
{
   // Agent implementation - MCP tools auto-registered
}
```

**2. MCP Server Configuration**
```json
{
  "MCP": {
    "Servers": [
      {
        "Name": "MonkeyMCP",
        "Uri": "https://func-monkeymcp-3t4eixuap5dfm.azurewebsites.net/",
        "Enabled": true,
        "AdditionalSettings": {}
      }
    ]
  }
}
```

**3. Automatic Discovery & Integration**
At agent creation, Morgana:
- Connects to declared MCP servers
- Queries available tools via `tools/list`
- Extracts JSON Schema parameter definitions
- Generates typed delegates with proper parameter names using **Reflection.Emit**
- Registers tools alongside native Morgana tools

**4. Type-Safe Parameter Handling**
MCP tools support rich typing through JSON Schema → CLR type mapping:
```json
{
  "name": "calculate_stats",
  "inputSchema": {
    "properties": {
      "count": { "type": "integer" },
      "active": { "type": "boolean" },
      "threshold": { "type": "number" }
    }
  }
}
```

Morgana automatically generates:
```csharp
Func<int, bool, double, Task<object>> calculate_stats
```

**Supported Types:**
- `string` → `string`
- `integer` → `int`
- `number` → `double`
- `boolean` → `bool`

**5. Transparent Tool Usage**
From the agent's perspective (and Morgana's orchestration layer), **there's no distinction** between native tools and MCP-acquired tools:

```csharp
// Both execute identically through AIFunction interface
await aiAgent.ExecuteAsync("GetInvoices", args);      // Native tool
await aiAgent.ExecuteAsync("get_monkey_journey", args); // MCP tool
```

### Real-World Example: MonkeyAgent

The `MonkeyAgent` demonstrates MCP integration with a playful example:

**Agent Declaration:**
```csharp
[HandlesIntent("monkeys")]
[UsesMCPServers("MonkeyMCP")]
public class MonkeyAgent : MorganaAgent
{
    // Agent gains 5 monkey-related tools automatically!
}
```

**Acquired MCP Tools:**
- `get_monkeys`: List all available monkeys
- `get_monkey(name)`: Get details for a specific monkey
- `get_monkey_journey(name)`: Generate unique journey with activities and health stats
- `get_all_monkey_journeys`: Get journey paths for all monkeys
- `get_monkey_business`: Random monkey emoji fun 🐵

**User Interaction:**
```
User: "Tell me about Curious George"
Agent → Calls get_monkey("Curious George") via MCP
      → Receives monkey details
      → Synthesizes natural language response
```

### Implementation: Rock-Solid Industrial MCP

Morgana's MCP integration is built on **Microsoft's official ModelContextProtocol library**, ensuring industrial-grade reliability and compliance with the MCP specification.

**Key Components:**
- **MCPClient**: HTTP/SSE transport with `tools/list` and `tools/call` support
- **MCPClientService**: Multi-server connection management
- **MCPAdapter**: JSON Schema → ToolDefinition conversion with type safety
- **AgentAdapter**: Automatic tool registration during agent initialization

**Technical Highlights:**
- **DynamicMethod IL generation** for parameter name preservation (required by `AIFunctionFactory`)
- **Mixed-type parameter support** with automatic boxing for value types
- **Object array executor pattern** enabling unlimited parameter counts
- **Type conversion layer** ensuring JSON serialization compatibility

### Configuration & Extensibility

**Enable MCP for any agent:**
```csharp
[HandlesIntent("your-domain")]
[UsesMCPServers("ServerA", "ServerB")]  // Multiple servers supported!
public class YourAgent : MorganaAgent { }
```

**Add new MCP servers in appsettings.json:**
```json
{
  "MCP": {
    "Servers": [
      {
        "Name": "YourServer",
        "Uri": "http://your-service:8080",
        "Enabled": true,
        "AdditionalSettings": {}
      }
    ]
  }
}
```

No code changes required—just configuration and attribute decoration!

### Why This Matters

**For Developers:**
- Add capabilities without writing tool implementations
- Integrate third-party services instantly
- Prototype new features rapidly

**For System Architects:**
- Microservices can expose tools via MCP
- Shared capabilities across multiple agents
- Clear separation between orchestration (Morgana) and execution (MCP servers)

**For Business:**
- Faster time-to-market for new features
- Reduced development and maintenance costs
- Ecosystem of reusable MCP tools

Morgana treats MCP tools as **first-class citizens**, making external capabilities feel native and eliminating the artificial boundary between "built-in" and "integrated" functionality. 🐒✨

## Technology Stack

### Core Framework
- **.NET 10**: Leveraging the latest C# features, performance improvements, and native AOT capabilities
- **ASP.NET Core Web API**: RESTful interface for client interactions
- **Akka.NET 1.5**: Actor-based concurrency model for resilient, distributed agent orchestration

### AI & Agent Framework
- **Microsoft.Extensions.AI**: Unified abstraction over chat completions with `IChatClient` interface
- **Microsoft.Agents.AI**: Declarative agent definition with built-in tool calling, `AgentThread` for conversation history, and `AIContextProvider` for state management
- **Azure OpenAI Service / Anthropic Claude**: Multi-provider LLM support through configurable implementations

### Memory & Context Management
- **AgentThread**: Framework-native conversation history management
- **MorganaContextProvider**: Custom `AIContextProvider` implementation for stateful context management with temporary variable support
- **P2P Context Sync**: Actor-based broadcast mechanism for shared context variables
- **Quick Replies Storage**: Temporary context variables for LLM-generated interactive buttons

### LLM Provider Support

Morgana provides native support for multiple LLM providers through a pluggable architecture:

**MorganaLLMService**
- Abstract base class for all LLM service implementations
- Defines the contract for guardrail validation, intent classification, and agent tool execution
- Ensures consistent behavior across different provider implementations

**Supported Providers:**
- **Azure OpenAI Service**: GPT-4 powered language understanding and generation
- **Anthropic Claude**: Claude Sonnet 4.5 with advanced reasoning capabilities

**Configuration:**
LLM provider selection is managed through `appsettings.json`:

```json
{
  "LLM": {
    "Provider": "AzureOpenAI"  // or "Anthropic"
  }
}
```

The dependency injection container automatically resolves the appropriate `ILLMService` implementation based on the configured provider, allowing seamless switching between LLM backends without code changes.

### Tool & Function Calling

Tools are defined as C# delegates mapped to tool definitions from prompts. The `ToolAdapter` provides runtime validation and dynamic function creation:

```csharp
[ProvidesToolForIntent("billing")]
// Define tool implementation with lazy context provider access
public class BillingTool : MorganaTool
{
  public BillingTool(
    ILogger<MorganaAgent> logger,
    Func<MorganaContextProvider> getContextProvider) : base(logger, getContextProvider) { }

    public async Task<string> GetInvoices(string userId, int count = 3)
    {
       // Implementation
    }
}

// Register with adapter in AgentAdapter
toolAdapter.AddTool("GetInvoices", billingTool.GetInvoices, toolDefinition);

// Create AIFunction for agent
AIFunction function = toolAdapter.CreateFunction("GetInvoices");
```

**Lazy Context Provider Access:**
Tools receive `Func<MorganaContextProvider>` to access context on-demand:

```csharp
public Task<object> GetContextVariable(string variableName)
{
  MorganaContextProvider provider = getContextProvider();
  object? value = provider.GetVariable(variableName);
   // ...
}
```

This lazy accessor pattern ensures tools always interact with the current context state without tight coupling.

**Validation:**
- Parameter count and names must match between implementation and definition
- Required vs. optional parameters are validated
- Type mismatches are caught at registration time

The Agent Framework automatically:
1. Exposes tool schemas to the LLM
2. Handles parameter validation and type conversion
3. Invokes the appropriate method
4. Returns results to the LLM for natural language synthesis

---
