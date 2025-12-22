<table style="border:none;">
  <tr>
    <td width="256">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="256"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A modern and flexible multi-agent, intent-driven conversational AI system</strong></p>
      <p>
        <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10"/>
        <img src="https://img.shields.io/badge/Akka.NET-512BD4?logo=nuget" alt="Akka.NET"/>
        <img src="https://img.shields.io/badge/Microsoft.Agents.AI-512BD4?logo=nuget" alt="Microsoft.Agents.AI"/>
      </p>
    </td>
  </tr>
</table>

## Overview

Morgana is a modern conversational AI system designed to handle complex scenarios through a sophisticated multi-agent intent-driven architecture. Built on cutting-edge .NET 10 and leveraging the actor model via Akka.NET, Morgana orchestrates specialized AI agents that collaborate to understand, classify, and resolve customer inquiries with precision and context awareness.

The system is powered by Microsoft.Agents.AI framework, enabling seamless integration with Large Language Models (LLMs) while maintaining strict governance through guard rails and policy enforcement.

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

## Architecture

### High-Level Component Flow

```
┌───────────────────────────────────────────────────────────────┐
│                         User Request                          │
└──────────────────────────────┬────────────────────────────────┘
                               │
                               ▼
┌───────────────────────────────────────────────────────────────┐
│                   ConversationManagerActor                    │
│  (Coordinates, routes, manages stateful conversational flow)  │
└──────────────────────────────┬────────────────────────────────┘
                               │
                               ▼
┌───────────────────────────────────────────────────────────────┐
│                  ConversationSupervisorActor                  │
│  (Orchestrates the entire multi-turn conversation lifecycle)  │
└───┬───────────┬───────────────┬───────────────────────────────┘
    │           │               │
    ▼           ▼               ▼
┌───────┐  ┌──────────┐   ┌───────────┐
│ Guard │  │Classifier│   │   Router  │ ← Context Sync Bus
│ Actor │  │  Actor   │   │   Actor   │
└───────┘  └──────────┘   └───────────┘
    │           │               │
    │           │               ▼
    │           │         ┌───────────┐
    │           │         │  Morgana  │
    │           │         │   Agent   │
    │           │         └───────────┘
    │           │               └──────────────┬────────────┬─...fully extensible
    │           │               │              │            │
    │           │               ▼              ▼            ▼
    │           │         ┌──────────┐   ┌───────────┐  ┌─────────────────┐
    │           │         │ Billing* │   │ Contract* │  │ Troubleshooting*│
    │           │         │  Agent   │   │   Agent   │  │     Agent       │
    │           │         └──────────┘   └───────────┘  └─────────────────┘
    │           │              │               │            │
    │           │              │   ┌───────────┴────────────┘
    │           │              │   │
    │           │              ▼   ▼
    │           │         ┌──────────────────────┐
    │           │         │MorganaContextProvider│ ← AIContextProvider
    │           │         │  (Context + Thread)  │   (Microsoft.Agents.AI)
    │           │         └──────────────────────┘
    │           │              │
    │           │              │ P2P Context Sync via RouterActor
    │           │              │
    └─────┬─────┘              │
          │                    │
          ▼                    ▼
         ┌─────────────────────────────────────────────┐
         │           MorganaLLMService                 │
         │  (Guardrail, Classification, Tool Calling)  │
         └─────────────────────┬───────────────────────┘
                               │
                               ▼
               ┌───────────────────────────────┐
               │             LLM               │
               │ (AzureOpenAI, Anthropic, ...) │
               └───────────────────────────────┘
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
Guard behavior is defined in `prompts.json` with customizable violation terms and responses, making policy updates deployment-independent.

#### 4. ClassifierActor
An intelligent classifier actor that analyzes user intent and determines the appropriate handling path.

**Intent Recognition:**
The classifier identifies specific intents configured in `prompts.json`. **Example** built-in intents include:
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
Specialized agents with domain-specific knowledge and tool access. The system includes three built-in **example** agents, but **the architecture is fully extensible** to support any domain-specific intent.

**BillingAgent** (example)
- **Tools**: `GetInvoices()`, `GetInvoiceDetails()`
- **Purpose**: Handle all billing inquiries, payment verification, and invoice retrieval
- **Prompt**: Defined in `prompts.json` under ID "Billing"

**TroubleshootingAgent** (example)
- **Tools**: `RunDiagnostics()`, `GetTroubleshootingGuide()`
- **Purpose**: Diagnose connectivity issues, provide step-by-step troubleshooting
- **Prompt**: Defined in `prompts.json` under ID "Troubleshooting"

**ContractAgent** (example)
- **Tools**: `GetContractDetails()`, `InitiateCancellation()`
- **Purpose**: Handle contract modifications and termination requests
- **Prompt**: Defined in `prompts.json` under ID "Contract"

**Adding Custom Agents:**
To add a new agent for your domain:
1. Define the intent in `prompts.json` (Classifier section)
2. Create a prompt configuration for the agent behavior
3. Implement a new class inheriting from `MorganaAgent`
4. Decorate with `[HandlesIntent("your_intent")]`
5. Define tools and inject via `AgentAdapter`

The `AgentRegistryService` automatically discovers and validates all agents at startup.

## Conversational Memory & Context Management

Morgana leverages **Microsoft.Agents.AI framework** for native conversation history and context management, eliminating the need for manual memory handling.

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
Parameters are marked as `Shared: true` in `prompts.json`:

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
    contextProviderLogger, 
    sharedVariables);

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

## Prompt Management System

Morgana treats prompts as **first-class project artifacts**, not hardcoded strings. The `IPromptResolverService` provides a flexible, maintainable approach to prompt engineering.

### Key Components

**IPromptResolverService**
- Centralizes prompt retrieval and resolution
- Supports structured prompt definitions with metadata
- Enables versioning and A/B testing of prompts
- Validates prompt consistency across agents

**ConfigurationPromptResolverService**
- Loads prompts from embedded `prompts.json` resource
- Parses structured prompt definitions including:
  - System instructions
  - Tool definitions
  - Additional properties (guard terms, error messages, context guidance)
  - Language and versioning metadata

**Prompt Structure**
```json
{
  "ID": "billing",
  "Type": "INTENT",
  "SubType": "AGENT",
  "Content": "Agent personality and role description",
  "Instructions": "Behavioral rules and token conventions",
  "AdditionalProperties": [
    {
      "Tools": [
        {
          "Name": "GetInvoices",
          "Description": "Retrieves user invoices",
          "Parameters": [...]
        }
      ]
    }
  ]
}
```

### Agent Instructions Composition

Each agent receives comprehensive instructions at creation time:

```csharp
instructions: $"{morganaPrompt.Content}\n{morganaPrompt.Instructions}\n\n{agentPrompt.Content}\n{agentPrompt.Instructions}"
```

This ensures:
1. **Global Rules**: Morgana's system-wide instructions (context handling, tone, policies)
2. **Agent-Specific Behavior**: Specialized instructions for the domain (billing, troubleshooting, etc.)
3. **No Duplication**: Instructions are set once at agent creation, not repeated in conversation history

### Context Handling Guidance

The system provides parameter-level guidance through `AdditionalProperties`:

```json
{
  "ContextToolParameterGuidance": "CONTEXT: First check if it exists with GetContextVariable. If missing, ask the user",
  "RequestToolParameterGuidance": "DIRECT REQUEST: This value must be provided by the user in the current message, do not use context"
}
```

These templates are automatically applied to tool parameters based on their `Scope` attribute, ensuring consistent LLM behavior.

### Benefits
- **Separation of Concerns**: Prompt engineering decoupled from application logic
- **Rapid Iteration**: Update agent behavior without recompiling
- **Consistency**: Single source of truth for agent instructions
- **Auditability**: Version-controlled prompt evolution
- **Localization Ready**: Multi-language support built-in

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
- **MorganaContextProvider**: Custom `AIContextProvider` implementation for stateful context management
- **P2P Context Sync**: Actor-based broadcast mechanism for shared context variables

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
// Define tool implementation with lazy context provider access
public class BillingTool : MorganaTool
{
    public BillingTool(
        ILogger<MorganaAgent> logger,
        Func<MorganaContextProvider> getContextProvider) 
        : base(logger, getContextProvider) { }

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

**Built with ❤️ using .NET 10, Akka.NET and Microsoft.Agents.AI**
