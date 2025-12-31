<table style="border:none;">
  <tr>
    <td width="256">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="256"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A modern and flexible multi-agent, intent-driven conversational AI framework with MCP Protocol support</strong></p>
      <p>
        <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10"/>
        <img src="https://img.shields.io/badge/Akka.NET-512BD4?logo=nuget" alt="Akka.NET"/>
        <img src="https://img.shields.io/badge/Microsoft.Agents.AI-512BD4?logo=nuget" alt="Microsoft.Agents.AI"/>
        <img src="https://img.shields.io/badge/MCP-Protocol-512BD4" alt="MCP Protocol"/>
      </p>
    </td>
  </tr>
</table>

## Overview

Morgana is a modern conversational AI framework designed to handle complex scenarios through a sophisticated multi-agent intent-driven architecture with **Model Context Protocol (MCP)** integration. Built on cutting-edge .NET 10 and leveraging the actor model via Akka.NET, Morgana orchestrates specialized AI agents that collaborate to understand, classify, and resolve customer inquiries with precision and context awareness.

The system is powered by Microsoft.Agents.AI framework and **MCP Protocol**, enabling seamless integration with Large Language Models (LLMs) and **dynamic tool expansion** through declarative server dependencies, while maintaining strict governance through guard rails and policy enforcement.

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
10. **MCP Protocol Integration**: Dynamic capability expansion through declarative server dependencies
11. **Registry-Based Validation**: Fail-fast MCP server configuration validation at startup

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
    │           │         │ Billing* │   │ Contract* │  │ Troubleshooting*│ ← [UsesMCPServers]
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
    │           │              ▼
    │           │         ┌──────────────────────────────────────┐
    │           │         │        MorganaMCPToolProvider        │
    │           │         │  • Tool Discovery & Loading          │
    │           │         │  • AIFunction Conversion             │
    │           │         │  • Server Registry Integration       │
    │           │         └──────────────┬───────────────────────┘
    │           │                        │
    │           │                        ▼
    │           │         ┌──────────────────────────────────────┐
    │           │         │     UsesMCPServerRegistryService     │
    │           │         │  • Agent → Server Mapping            │
    │           │         │  • Configuration Validation          │
    │           │         │  • Fail-Fast Checks                  │
    │           │         └──────────────┬───────────────────────┘
    │           │                        │
    │           │                        ▼
    │           │         ┌──────────────────────────────────────┐
    │           │         │         MorganaMCPServer             │
    │           │         │  (In-Process / HTTP [coming soon])   │
    │           │         ├──────────────────────────────────────┤
    │           │         │  • HardwareCatalogMCPServer          │
    │           │         │  • SecurityCatalogMCPServer          │
    │           │         │  • [Your Custom MCP Servers]         │
    │           │         │  • [HTTP Remote Servers - v0.6+]     │
    │           │         └──────────────────────────────────────┘
    │           │
    └─────┬─────┘
          │
          ▼
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
- `troubleshooting`: Diagnose connectivity or device issues (🆕 **with MCP catalog integration**)
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
- **MCP Servers**: None (uses only native tools)
- **Purpose**: Handle all billing inquiries, payment verification, and invoice retrieval
- **Prompt**: Defined in `prompts.json` under ID "Billing"

**TroubleshootingAgent** (example) 🆕
- **Tools**: `RunDiagnostics()`, `GetTroubleshootingGuide()`
- **🆕 MCP Servers**: `HardwareCatalog`, `SecurityCatalog` (automatically loaded via `[UsesMCPServers]`)
- **🆕 MCP Tools**: `CercaHardwareCompatibile()`, `OttieniSpecificheHardware()`, `CercaSoftwareSicurezza()`, `OttieniDettagliSoftwareSicurezza()`, `VerificaCompatibilitaMinaccia()`
- **Purpose**: Diagnose connectivity issues, recommend hardware/software solutions from real product catalogs
- **Prompt**: Defined in `prompts.json` under ID "Troubleshooting"

**ContractAgent** (example)
- **Tools**: `GetContractDetails()`, `InitiateCancellation()`
- **MCP Servers**: None (uses only native tools)
- **Purpose**: Handle contract modifications and termination requests
- **Prompt**: Defined in `prompts.json` under ID "Contract"

**Adding Custom Agents:**
To add a new agent for your domain:
1. Define the intent in `prompts.json` (Classifier section)
2. Create a prompt configuration for the agent behavior
3. Implement a class inheriting `MorganaAgent`
4. Decorate with `[HandlesIntent("your-intent")]`
5. **🆕 Optionally decorate with `[UsesMCPServers("Server1", "Server2")]` to load external tools**
6. Optionally create native `MorganaTool` implementations
7. Register tool definitions in the agent's prompt configuration

The system automatically discovers and registers your agent through the `AgentRegistryService` and `MCPServerRegistryService`.

## 🆕 Model Context Protocol (MCP) Integration

Morgana v0.5.0 introduces **first-class support for the Model Context Protocol**, enabling agents to dynamically expand their capabilities by declaring dependencies on MCP servers.

### What is MCP?

The **Model Context Protocol** is a standardized interface for providing tools and context to Large Language Models. It allows LLMs to access:
- External data sources (databases, APIs, file systems)
- Computational tools (calculators, code execution, data analysis)
- Domain-specific services (catalogs, CRMs, knowledge bases)

Morgana implements MCP to enable **declarative tool expansion**: instead of hardcoding every tool an agent needs, you simply declare which MCP servers it should use.

### MCP Architecture in Morgana

```
┌─────────────────────────────────────────────────────────┐
│                   MorganaAgent                          │
│                                                         │
│  [HandlesIntent("troubleshooting")]                     │
│  [UsesMCPServers("HardwareCatalog", "SecurityCatalog")]│ ← Declarative!
│                                                         │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────┐
│          AgentAdapter.CreateAgent()                     │
│  • Queries IMCPServerRegistryService                    │
│  • Loads tools from declared servers                    │
│  • Merges with native tools                             │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────┐
│         IMCPServerRegistryService                       │
│  • Maps: Type agentType → string[] serverNames          │
│  • Validates configuration at startup                   │
│  • Fails fast if servers are missing                    │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────┐
│            IMCPToolProvider                             │
│  • Discovers tools from registered servers              │
│  • Converts MCPToolDefinition → AIFunction              │
│  • Handles tool invocation routing                      │
└────────────────────────┬────────────────────────────────┘
                         │
         ┌───────────────┴───────────────┐
         │                               │
         ▼                               ▼
┌──────────────────┐          ┌──────────────────┐
│ HardwareCatalog  │          │ SecurityCatalog  │
│   MCPServer      │          │   MCPServer      │
├──────────────────┤          ├──────────────────┤
│ In-Process       │          │ In-Process       │
│ (Mock/Example)   │          │ (Mock/Example)   │
└──────────────────┘          └──────────────────┘

         🔜 Coming in v0.6+
         ▼
┌──────────────────────────────────┐
│     HTTP MCP Servers             │
│  • Remote tool providers         │
│  • Third-party integrations      │
│  • Production-grade catalogs     │
└──────────────────────────────────┘
```

### Declaring MCP Server Dependencies

Agents declare MCP server requirements using the `[UsesMCPServers]` attribute:

```csharp
[HandlesIntent("troubleshooting")]
[UsesMCPServers("HardwareCatalog", "SecurityCatalog")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<TroubleshootingAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        AgentAdapter agentAdapter) 
        : base(conversationId, llmService, promptResolverService, logger)
    {
        // MCP tools automatically loaded from declared servers
        (aiAgent, contextProvider) = agentAdapter.CreateAgent(GetType(), OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}
```

**That's it!** No manual tool registration, no hardcoded dependencies. The agent automatically receives:
- `CercaHardwareCompatibile(tipoComponente, modelloAttuale?, tipoProblema?)`
- `OttieniSpecificheHardware(modello)`
- `CercaSoftwareSicurezza(tipoSoftware, tipoMinaccia?, sintomi?)`
- `OttieniDettagliSoftwareSicurezza(nomeProdotto)`
- `VerificaCompatibilitaMinaccia(nomeMinaccia)`

### MCP Server Configuration

MCP servers are configured in `appsettings.json`:

```json
{
  "LLM": {
    "MCPServers": [
      {
        "Name": "HardwareCatalog",
        "Type": "InProcess",
        "Enabled": true
      },
      {
        "Name": "SecurityCatalog",
        "Type": "InProcess",
        "Enabled": true
      }
    ]
  }
}
```

**Configuration Validation:**
At startup, `UsesMCPServerRegistryService` validates that:
- All servers declared by agents are configured
- All configured servers are enabled
- Server implementations exist in the assembly

**Example validation output:**
```
✓ MCP Validation: Agent 'troubleshooting' has all required servers: HardwareCatalog, SecurityCatalog
```

Or if misconfigured:
```
⚠️  MCP VALIDATION WARNING: Agent 'troubleshooting' requires MCP servers that are not configured or enabled:
   Missing: HardwareCatalog
   Available: SecurityCatalog
   → Please enable these servers in appsettings.json under LLM:MCPServers
```

### Creating Custom MCP Servers

Implement `MorganaMCPServer` for in-process tool providers:

```csharp
public class HardwareCatalogMCPServer : MorganaMCPServer
{
    public HardwareCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger<HardwareCatalogMCPServer> logger,
        IConfiguration configuration) 
        : base(config, logger, configuration)
    {
        // Initialize catalog, connect to database, etc.
    }

    protected override Task<IEnumerable<Records.MCPToolDefinition>> RegisterToolsAsync()
    {
        // Define tool schemas
        Records.MCPToolDefinition[] tools = [
            new Records.MCPToolDefinition(
                Name: "CercaHardwareCompatibile",
                Description: "Search for compatible hardware components...",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["tipoComponente"] = new(
                            Type: "string",
                            Description: "Component type to search",
                            Enum: ["router", "modem", "extender"])
                    },
                    Required: ["tipoComponente"]))
        ];
        
        return Task.FromResult<IEnumerable<Records.MCPToolDefinition>>(tools);
    }

    protected override Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        return toolName switch
        {
            "CercaHardwareCompatibile" => SearchCompatibleHardwareAsync(parameters),
            _ => Task.FromResult(new Records.MCPToolResult(true, null, $"Unknown tool: {toolName}"))
        };
    }

    private Task<Records.MCPToolResult> SearchCompatibleHardwareAsync(
        Dictionary<string, object> parameters)
    {
        // Use TryGetNormalizedParameter for LLM-tolerant parameter extraction
        if (!TryGetNormalizedParameter(parameters, "tipoComponente", out object? componentTypeObj))
        {
            return Task.FromResult(new Records.MCPToolResult(true, null, "Missing 'tipoComponente'"));
        }
        
        string componentType = componentTypeObj?.ToString() ?? "";
        
        // Query catalog, format results
        string content = "Found 3 compatible routers...";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
}
```

**Key Features:**
- **`TryGetNormalizedParameter()`**: Handles LLM parameter name variations (camelCase, snake_case, etc.)
- **Configurable normalization**: Min substring length and similarity thresholds in `appsettings.json`
- **Automatic DI**: Servers are instantiated with constructor injection

### MCP Roadmap

**v0.5.0 (Current)**
- ✅ In-process MCP server support
- ✅ Declarative agent dependencies via `[UsesMCPServers]`
- ✅ Automatic tool discovery and registration
- ✅ Configuration validation with fail-fast checks
- ✅ Example catalog servers (Hardware, Security)

**v0.6.0 (Planned - Q2 2025)**
- 🔜 HTTP MCP client implementation
- 🔜 Remote MCP server integration
- 🔜 Third-party MCP provider support
- 🔜 MCP server discovery protocol
- 🔜 Tool caching and performance optimization

### Benefits of MCP Integration

**For Developers:**
- **Declarative tool management**: No manual wiring, just attributes
- **Separation of concerns**: Tool logic lives in dedicated MCP servers
- **Testability**: Mock MCP servers for unit testing
- **Reusability**: Share MCP servers across multiple agents

**For Operations:**
- **Configuration-driven**: Enable/disable servers without recompiling
- **Fail-fast validation**: Configuration errors caught at startup
- **Performance**: Tools loaded once and cached

**For Business:**
- **Rapid capability expansion**: Add new tools by deploying MCP servers
- **Integration flexibility**: Connect to any data source via MCP
- **Vendor independence**: Standard protocol, multiple implementations

## Agent Registration & Discovery

Morgana uses **declarative attribute-based mapping** for both intent handling and MCP server dependencies, enabling automatic discovery and validation.

### Intent Handling with `[HandlesIntent]`

**1. Declarative Intent Mapping**
```csharp
[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    // Agent implementation
}
```

**2. Automatic Discovery**
The `IAgentRegistryService` scans the assembly at startup to find all classes:
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

### 🆕 MCP Server Dependencies with `[UsesMCPServers]`

**1. Declarative Server Dependencies**
```csharp
[HandlesIntent("troubleshooting")]
[UsesMCPServers("HardwareCatalog", "SecurityCatalog")]
public class TroubleshootingAgent : MorganaAgent
{
    // Agent implementation
}
```

**2. Automatic Discovery**
The `IMCPServerRegistryService` scans the assembly at startup to find all classes:
- Inheriting from `MorganaAgent`
- Decorated with `[UsesMCPServers]`

**3. Configuration Validation**
The system enforces consistency between:
- MCP servers declared in agent classes
- MCP servers configured in `appsettings.json`

At startup, it verifies:
- Every declared server is configured and enabled
- Server implementations exist in the assembly

**4. Runtime Resolution**
```csharp
string[] serverNames = mcpServerRegistryService.GetServerNamesForAgent(typeof(TroubleshootingAgent));
// Returns ["HardwareCatalog", "SecurityCatalog"]
```

### Validation Guarantees

The system throws explicit exceptions or logs warnings if:
- A classifier intent has no corresponding agent
- An agent declares an intent not configured for classification
- **🆕 An agent declares MCP servers that are not configured**
- **🆕 An agent declares MCP servers that are disabled**
- **🆕 An MCP server implementation is missing**
- Tool definitions mismatch between prompts and implementations

This **fail-fast approach** ensures configuration errors are caught at startup, not during customer interactions.

### Complete Agent Example

```csharp
[HandlesIntent("troubleshooting")]
[UsesMCPServers("HardwareCatalog", "SecurityCatalog")]
public class TroubleshootingAgent : MorganaAgent
{
    public TroubleshootingAgent(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger<TroubleshootingAgent> logger,
        ILogger<MorganaContextProvider> contextProviderLogger,
        AgentAdapter agentAdapter) 
        : base(conversationId, llmService, promptResolverService, logger)
    {
        // Automatic loading of:
        // - Native tools from TroubleshootingTool
        // - MCP tools from HardwareCatalog + SecurityCatalog
        (aiAgent, contextProvider) = agentAdapter.CreateAgent(
            GetType(), 
            OnSharedContextUpdate);

        ReceiveAsync<Records.AgentRequest>(ExecuteAgentAsync);
    }
}
```

**What happens automatically:**
1. `AgentAdapter` queries `IMCPServerRegistryService` for server names
2. `IMCPToolProvider` loads tools from each declared server
3. Tools are converted to `AIFunction` instances
4. Native tools + MCP tools are merged
5. Agent is created with complete toolset

No manual tool registration, no configuration files, no boilerplate code.

## P2P Context Synchronization

Agents can share context variables through a **first-write-wins** broadcast mechanism orchestrated by `RouterActor`.

### How It Works

**1. Shared Variable Declaration**
In `prompts.json`, mark tool parameters as shared:

```json
{
  "Parameters": [
    {
      "Name": "userId",
      "Scope": "context",
      "Shared": true  ← Broadcasts to other agents
    }
  ]
}
```

**2. Automatic Broadcasting**
When `MorganaContextProvider.SetVariable()` is called on a shared variable:

```csharp
contextProvider.SetVariable("userId", "P994E");
```

The provider:
- Stores the value locally
- Invokes `OnSharedContextUpdate` callback
- Callback sends `BroadcastContextUpdate` to `RouterActor`

**3. Router Broadcasting**
`RouterActor` broadcasts the update to all other agents:

```csharp
foreach (var agent in agents.Where(a => a.Key != sourceAgentIntent))
{
    agent.Value.Tell(new ReceiveContextUpdate(sourceAgentIntent, updatedValues));
}
```

**4. Recipient Handling**
Each agent receives the update via `HandleContextUpdate()`:

```csharp
private void HandleContextUpdate(Records.ReceiveContextUpdate msg)
{
    contextProvider.MergeSharedContext(msg.UpdatedValues);
}
```

**5. First-Write-Wins Merge**
`MergeSh aredContext()` accepts only variables **not already present**:

```csharp
public void MergeSharedContext(Dictionary<string, object> sharedContext)
{
    foreach (var kvp in sharedContext)
    {
        if (!AgentContext.ContainsKey(kvp.Key))
        {
            AgentContext[kvp.Key] = kvp.Value;
            logger.LogInformation($"MERGED shared context '{kvp.Key}'");
        }
        else
        {
            logger.LogInformation($"IGNORED shared context '{kvp.Key}' (already set)");
        }
    }
}
```

### Example Scenario

1. User talks to `BillingAgent` and provides `userId = "P994E"`
2. `BillingAgent` calls `SetContextVariable("userId", "P994E")`
3. Variable is marked as `Shared: true` in tool definition
4. `RouterActor` broadcasts to `ContractAgent` and `TroubleshootingAgent`
5. Both agents now have `userId` in their context
6. User switches to `ContractAgent` → no need to re-ask for `userId`

### Benefits
- **Seamless handoffs**: User doesn't repeat information
- **Natural conversations**: Context flows between specialists
- **Decoupled agents**: No direct agent-to-agent dependencies
- **First-write-wins**: Prevents context conflicts

## Prompt Engineering & Configuration

All agent behavior, policies, and tool definitions are externalized in `prompts.json`. This enables non-developers to iterate on agent behavior without touching code.

### Prompt Structure

Each prompt entry in `prompts.json` follows this schema:

```json
{
  "ID": "Unique identifier (e.g., 'Billing', 'Guard', 'Classifier')",
  "Type": "SYSTEM | INTENT",
  "SubType": "AGENT | ACTOR | PRESENTATION",
  "Content": "Core agent identity and purpose",
  "Instructions": "Behavioral guidelines and rules",
  "Personality": "Optional: agent-specific personality traits",
  "Language": "Language code (e.g., 'it-IT', 'en-US')",
  "Version": "Semantic version for change tracking",
  "AdditionalProperties": [
    {
      "Tools": [ ... ],
      "GlobalPolicies": [ ... ],
      "ErrorAnswers": [ ... ]
    }
  ]
}
```

### Example: Billing Agent Prompt

```json
{
  "ID": "Billing",
  "Type": "INTENT",
  "SubType": "AGENT",
  "Content": "You are a specialized billing assistant...",
  "Instructions": "Never invent information. Always use tools...",
  "Personality": "Professional, precise, empathetic...",
  "Language": "it-IT",
  "Version": "1",
  "AdditionalProperties": [
    {
      "Tools": [
        {
          "Name": "GetInvoices",
          "Description": "Retrieves user invoices for a specified period",
          "Parameters": [
            {
              "Name": "userId",
              "Description": "User's alphanumeric identifier",
              "Required": true,
              "Scope": "context",
              "Shared": true
            },
            {
              "Name": "count",
              "Description": "Number of recent invoices to retrieve",
              "Required": true,
              "Scope": "request"
            }
          ]
        }
      ]
    }
  ]
}
```

### Global Policies

Global policies are enforced across **all agents** to ensure consistency. Defined in the `Morgana` prompt:

```json
{
  "GlobalPolicies": [
    {
      "Name": "ContextHandling",
      "Description": "CRITICAL RULE - Always attempt GetContextVariable before asking user...",
      "Type": "Critical",
      "Priority": 0
    },
    {
      "Name": "InteractiveToken",
      "Description": "OPERATIONAL RULE - Use #INT# token when requiring user input...",
      "Type": "Operational",
      "Priority": 0
    },
    {
      "Name": "ToolParameterContextGuidance",
      "Description": "For context-scoped parameters, verify availability in context first...",
      "Type": "Operational",
      "Priority": 1
    },
    {
      "Name": "ToolParameterRequestGuidance",
      "Description": "For request-scoped parameters, derive from user query immediately...",
      "Type": "Operational",
      "Priority": 2
    }
  ]
}
```

### Error Handling Configuration

Standardized error messages are configured in `AdditionalProperties`:

```json
{
  "ErrorAnswers": [
    {
      "Name": "GenericError",
      "Content": "Sorry, I encountered a problem with an ingredient during potion preparation..."
    },
    {
      "Name": "LLMServiceError",
      "Content": "Sorry, the magic sphere refused to collaborate: ((llm_error))"
    }
  ]
}
```

Error messages support template placeholders (e.g., `((llm_error))`) for dynamic content injection.

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

## Technology Stack

### Core Framework
- **.NET 10**: Leveraging the latest C# features, performance improvements, and native AOT capabilities
- **ASP.NET Core Web API**: RESTful interface for client interactions
- **Akka.NET 1.5**: Actor-based concurrency model for resilient, distributed agent orchestration

### AI & Agent Framework
- **Microsoft.Extensions.AI**: Unified abstraction over chat completions with `IChatClient` interface
- **Microsoft.Agents.AI**: Declarative agent definition with built-in tool calling, `AgentThread` for conversation history, and `AIContextProvider` for state management
- **Azure OpenAI Service / Anthropic Claude**: Multi-provider LLM support through configurable implementations
- **🆕 Model Context Protocol (MCP)**: Standardized tool provider interface for dynamic capability expansion

### Memory & Context Management
- **AgentThread**: Framework-native conversation history management
- **MorganaContextProvider**: Custom `AIContextProvider` implementation for stateful context management
- **P2P Context Sync**: Actor-based broadcast mechanism for shared context variables

### 🆕 MCP Protocol Stack
- **IMCPServer**: Interface for local and remote MCP tool providers
- **IMCPToolProvider**: Orchestrates tool loading and AIFunction conversion
- **IMCPServerRegistryService**: Manages agent-to-server mappings with configuration validation
- **MorganaMCPServer**: Base class for in-process MCP server implementations
- **HTTP MCP Client** (coming in v0.6+): Support for remote MCP servers

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
    "Provider": "AzureOpenAI",  // or "Anthropic"
    "MCPServers": [
      {
        "Name": "HardwareCatalog",
        "Type": "InProcess",
        "Enabled": true
      }
    ]
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

**🆕 MCP Tool Loading:**
For agents with `[UsesMCPServers]`, the system additionally:
1. Queries `IMCPServerRegistryService` for server names
2. Loads `MCPToolDefinition` schemas from each server
3. Converts to `AIFunction` instances via `IMCPToolProvider`
4. Merges with native tools before agent creation

---

**Built with ❤️ using .NET 10, Akka.NET, Microsoft.Agents.AI and Model Context Protocol**

---

Morgana is developed in **Italy/Milan 🇮🇹**: we apologize if you find prompts and some code comments in Italian...but we invite you **at flying on the broomstick with Morgana 🧙‍♀️**
