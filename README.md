<table style="border:none;">
  <tr>
    <td width="256">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="256"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A domain-agnostic multi-agent conversational AI framework with full MCP Protocol support</strong></p>
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

Morgana is a **domain-agnostic conversational AI framework** built on .NET 10 and Akka.NET actors. It orchestrates multi-agent workflows with full **Model Context Protocol (MCP)** integration, enabling specialized AI assistants to collaborate seamlessly across any business domain or use case.

The framework provides enterprise-grade capabilities for intent-driven conversations, dynamic tool discovery, and context-aware agent orchestration—all while remaining completely independent from specific business domains. MCP tools can be discovered and connected both natively through **in-process integration** and via **standard MCP protocol over HTTP**, providing flexible deployment options for AI capabilities.

## Core Philosophy

Morgana addresses the fundamental challenges of building scalable, maintainable conversational AI systems:

1. **Domain Agnostic**: Pure framework with zero business logic—bring your own agents, tools, and use cases
2. **Agent Specialization**: Each agent has a single, well-defined responsibility with access to specific tools
3. **Actor-Based Concurrency**: Akka.NET provides fault tolerance, message-driven architecture, and natural scalability
4. **Intelligent Routing**: Requests are classified and routed to the most appropriate specialist agent
5. **Policy Enforcement**: A dedicated guard actor ensures all interactions comply with business rules and brand guidelines
6. **Declarative Configuration**: Prompts and agent behaviors are externalized as first-class project artifacts
7. **Automatic Discovery**: Agents self-register through attributes, eliminating manual configuration
8. **P2P Context Synchronization**: Agents share contextual information seamlessly through a message bus architecture
9. **Native Memory Management**: Context and conversation history managed by Microsoft.Agents.AI framework
10. **Personality-Driven Interactions**: Layered personality system with global and agent-specific traits
11. **Full MCP Integration**: Dynamic capability expansion through **InProcess** and **HTTP** MCP servers
12. **Registry-Based Validation**: Fail-fast MCP server configuration validation at startup
13. **Dynamic UI Context**: Visual agent identification with contextual branding in the user interface

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
│  • Orchestrates the entire multi-turn conversation lifecycle  │
│  • Tracks active agent context for dynamic UI updates         │
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
    │           │         │  Morgana  │ ← Base Agent (Framework Core)
    │           │         │   Agent   │
    │           │         └───────────┘
    │           │               └──────────────┬────────────┬─...fully extensible
    │           │               │              │            │
    │           │               ▼              ▼            ▼
    │           │         ┌──────────┐   ┌───────────┐  ┌─────────────────┐
    │           │         │ Custom   │   │  Custom   │  │     Custom      │ ← Domain Agents
    │           │         │ Agent A  │   │  Agent B  │  │    Agent C      │   (User-defined)
    │           │         └──────────┘   └───────────┘  └─────────────────┘
    │           │              │               │            │ [UsesMCPServers]
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
    │           │         │    (InProcess & HTTP Support)        │
    │           │         ├──────────────────────────────────────┤
    │           │         │  • InProcess: Direct .NET integration│
    │           │         │  • HTTP: Standard MCP over HTTP/SSE  │
    │           │         │  • [Your Custom MCP Servers]         │
    │           │         └──────────────┬───────────────────────┘
    │           │                        │
    └─────┬─────┘                        │
          │                              │
          ▼                              ▼
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

### Domain-Agnostic Framework Design

Morgana's core framework is **completely decoupled from business logic**. All domain-specific agents, tools, and MCP servers have been extracted into separate example projects:

**Framework Core (`Morgana`, `Morgana.AI`):**
- Actor system (ConversationManager, Supervisor, Guard, Classifier, Router)
- Base `MorganaAgent` class with personality system
- MCP protocol integration (InProcess + HTTP)
- Context provider and synchronization infrastructure
- LLM service abstractions
- Declarative configuration system

**Example Implementation (`Morgana.AI.Examples`):**
- Sample agents (Billing, Contract, Troubleshooting)
- Domain-specific tools (invoice retrieval, contract lookup, diagnostics)
- Example MCP servers (HardwareCatalog, SecurityCatalog)
- Reference prompts and configurations

This separation enables Morgana to power **any conversational AI use case**—from customer support to healthcare chatbots, from financial advisors to educational assistants—simply by implementing domain-specific agents and tools.

### Dynamic UI Agent Context

The user interface **automatically adapts** to show which agent is currently active:

**Visual Indicators:**
- **Header Display**: Shows "Morgana" for base conversations or "Morgana (AgentName)" when a specialist is active
- **Color Coding**: Violet border for base Morgana, pink border for specialized agents
- **Completion Messages**: Clear transition feedback when agents complete their tasks

**Technical Implementation:**
- `ConversationSupervisorActor` tracks the active agent's intent
- SignalR broadcasts agent name changes to connected clients
- Blazor UI updates header and avatar styling in real-time
- Inline styles ensure compatibility with Blazor Server's rendering model

This provides users with **contextual awareness** of which specialist is handling their request without any explicit user action.

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
- **Tracks active agent intent for UI context updates**
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
- Analyzes user messages using LLM-powered classification
- Maps intents to registered coordinator agents
- Handles multi-intent scenarios
- Maintains classification confidence metrics

**Dynamic Agent Discovery:**
Classifier automatically discovers all registered agents through the `[MorganaAgent]` attribute, eliminating manual configuration.

#### 5. RouterActor
The coordination hub that manages agent lifecycle and message routing.

**Capabilities:**
- Dynamic agent instantiation based on classification
- Request delegation to active agents
- Response collection and aggregation
- P2P context variable broadcasting
- Agent state management

#### 6. MorganaAgent (Base Class)
The foundation for all conversational agents in the system.

**Core Features:**
- Personality layer integration (global + agent-specific traits)
- Context provider access for stateful conversations
- Tool registration and management
- Declarative prompt binding
- Automatic MCP tool loading via `[UsesMCPServers]` attribute
- Thread-safe conversation history

**Extensibility:**
Domain-specific agents inherit from `MorganaAgent` and override:
```csharp
[MorganaAgent(Intent = "YourIntent")]
public class YourAgent : MorganaAgent
{
    protected override async Task RegisterToolsAsync() 
    { 
        // Register domain-specific tools
    }
}
```

### Model Context Protocol (MCP) Integration

Morgana provides **complete MCP support** with both **InProcess** and **HTTP** transport options:

#### InProcess MCP Servers
For maximum performance and .NET-native integration:

```csharp
public class YourMCPServer : MorganaMCPServer
{
    public YourMCPServer(ILogger<YourMCPServer> logger) 
        : base("YourServerName", logger) { }

    protected override void RegisterTools()
    {
        AddTool("YourTool", new MCPToolDefinition 
        {
            Name = "YourTool",
            Description = "Tool description",
            InputSchema = new { /* JSON schema */ }
        });
    }
    
    public override Task<object> ExecuteToolAsync(string toolName, Dictionary<string, object> arguments)
    {
        // Tool implementation
    }
}
```

**Benefits:**
- Zero serialization overhead
- Direct access to .NET libraries and dependencies
- Type safety with compile-time validation
- Integrated error handling and logging

#### HTTP MCP Servers
For distributed deployment and language-agnostic tool providers:

```json
{
  "LLM": {
    "MCPServers": [
      {
        "Name": "RemoteTools",
        "Type": "HTTP",
        "BaseUrl": "https://your-mcp-server.com",
        "Enabled": true
      }
    ]
  }
}
```

**Benefits:**
- Standard MCP protocol compliance
- Language-agnostic server implementations
- Horizontal scaling of tool providers
- Separation of concerns (framework vs. tools)
- SSE-based streaming for real-time updates

#### Declarative Agent-Server Binding

Agents declare their MCP dependencies using attributes:

```csharp
[MorganaAgent(Intent = "TechnicalSupport")]
[UsesMCPServers("DiagnosticTools", "HardwareCatalog")]
public class TechnicalSupportAgent : MorganaAgent
{
    // Tools from both servers automatically available
}
```

The registry service validates configurations at startup:
- Ensures all referenced servers are defined in `appsettings.json`
- Verifies server connectivity for HTTP servers
- Provides clear error messages for misconfigurations
- Enables fail-fast behavior to catch issues early

### Conversation Context & Memory

#### MorganaContextProvider
Custom implementation of `AIContextProvider` that manages conversation state:

**Capabilities:**
- Thread-safe variable storage
- Scope-based variable management (request vs. context)
- Integration with Microsoft.Agents.AI `AgentThread`
- P2P synchronization support

**Variable Scopes:**
```csharp
// Request-scoped: extracted from current message
context.SetRequestVariable("product_id", "ABC123");

// Context-scoped: persisted across conversation turns
context.SetContextVariable("user_preferences", preferences);
```

#### P2P Context Synchronization

Agents can broadcast context updates to other agents:

```csharp
// Agent A sets a shared variable
await BroadcastContextVariableAsync("customer_tier", "Premium");

// Agent B receives update automatically
string tier = context.GetContextVariable("customer_tier");
```

This enables seamless handoffs and collaborative problem-solving between specialized agents.

### Personality System

Morgana implements a **layered personality architecture** for consistent brand voice:

**Global Personality:**
Defined in `prompts.json` and applied to all agents:
```json
{
  "GlobalPersonality": {
    "Traits": ["friendly", "professional", "empathetic"],
    "Tone": "conversational",
    "VoiceGuidelines": "Use simple language, avoid jargon"
  }
}
```

**Agent-Specific Personality:**
Individual agents can extend or override traits:
```json
{
  "Agents": [
    {
      "Name": "TechnicalAgent",
      "Personality": {
        "AdditionalTraits": ["technical", "detail-oriented"],
        "Overrides": {
          "Tone": "instructional"
        }
      }
    }
  ]
}
```

**Runtime Composition:**
The framework merges global and agent-specific personalities at runtime, ensuring consistency while allowing specialization.

### Prompt Engineering as Code

All agent behaviors, policies, and personalities are **externalized to JSON configuration files**:

#### Structured Prompt Configuration

```json
{
  "Agents": [
    {
      "Name": "BillingAgent",
      "Intent": "Billing",
      "Personality": {
        "Traits": ["helpful", "precise", "reassuring"]
      },
      "SystemPrompt": "You are a billing specialist...",
      "Tools": [
        {
          "Name": "GetInvoices",
          "Description": "Retrieves customer invoices",
          "Parameters": [
            {
              "Name": "userId",
              "Type": "string",
              "Description": "Customer identifier",
              "Required": true,
              "Scope": "context",
              "Shared": true
            },
            {
              "Name": "count",
              "Type": "integer",
              "Description": "Number of invoices to retrieve",
              "Required": false,
              "Scope": "request"
            }
          ]
        }
      ]
    }
  ],
  "GlobalPolicies": [
    {
      "Name": "ToolParameterContextGuidance",
      "Content": "For context-scoped parameters, retrieve values from conversation context..."
    },
    {
      "Name": "ToolParameterRequestGuidance",
      "Content": "For request-scoped parameters, extract values from the current user message..."
    }
  ],
  "AdditionalProperties": {
    "ErrorAnswers": [
      {
        "Name": "GenericError",
        "Content": "I apologize, but I encountered an issue processing your request..."
      }
    ]
  }
}
```

#### Dynamic Parameter Guidance

Parameter-level guidance is automatically applied based on `Scope`:

```json
{
  "Parameters": [
    {
      "Name": "userId",
      "Scope": "context",
      "Shared": true
    },
    {
      "Name": "productId",
      "Scope": "request"
    }
  ]
}
```

The `ToolAdapter` enriches tool descriptions with scope-specific policies:
- **`Scope: "context"`**: Adds `ToolParameterContextGuidance` policy
- **`Scope: "request"`**: Adds `ToolParameterRequestGuidance` policy

**Benefits:**
- LLM understands where to source parameter values
- Reduces hallucination of parameter values
- Enables intelligent context reuse across agents
- Maintains conversation continuity through agent handoffs

#### Error Message Standardization

Standardized error messages are configured in `AdditionalProperties`:

```json
{
  "ErrorAnswers": [
    {
      "Name": "GenericError",
      "Content": "I apologize for the inconvenience. Let me try a different approach..."
    },
    {
      "Name": "LLMServiceError",
      "Content": "The AI service is temporarily unavailable: ((llm_error))"
    }
  ]
}
```

Error messages support template placeholders (e.g., `((llm_error))`) for dynamic content injection.

### Benefits of Declarative Configuration
- **Separation of Concerns**: Prompt engineering decoupled from application logic
- **Rapid Iteration**: Update agent behavior without recompiling or redeploying
- **Consistency**: Single source of truth for agent instructions and policies
- **Auditability**: Version-controlled prompt evolution
- **Localization Ready**: Multi-language support built-in
- **Policy Centralization**: Global rules applied uniformly across all agents
- **Error Consistency**: Standardized error messaging across the system
- **Domain Portability**: Same framework, different prompts = different use cases

## Technology Stack

### Core Framework
- **.NET 10**: Leveraging the latest C# features, performance improvements, and native AOT capabilities
- **ASP.NET Core Web API**: RESTful interface for client interactions
- **Akka.NET 1.5**: Actor-based concurrency model for resilient, distributed agent orchestration
- **Blazor Server**: Real-time UI with SignalR-powered state synchronization

### AI & Agent Framework
- **Microsoft.Extensions.AI**: Unified abstraction over chat completions with `IChatClient` interface
- **Microsoft.Agents.AI**: Declarative agent definition with built-in tool calling, `AgentThread` for conversation history, and `AIContextProvider` for state management
- **Azure OpenAI Service / Anthropic Claude**: Multi-provider LLM support through configurable implementations
- **Model Context Protocol (MCP)**: Standardized tool provider interface with **InProcess** and **HTTP** support

### Memory & Context Management
- **AgentThread**: Framework-native conversation history management
- **MorganaContextProvider**: Custom `AIContextProvider` implementation for stateful context management
- **P2P Context Sync**: Actor-based broadcast mechanism for shared context variables

### MCP Protocol Stack
- **IMCPServer**: Interface for local and remote MCP tool servers
- **IMCPToolProvider**: Orchestrates tool loading and AIFunction conversion
- **IMCPServerRegistryService**: Manages agent-to-server mappings with configuration validation
- **MorganaMCPServer**: Base class for in-process MCP server implementations
- **HTTP MCP Client**: Support for standard MCP over HTTP/SSE transport

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
        "Name": "YourServer",
        "Type": "InProcess",  // or "HTTP"
        "BaseUrl": "https://your-server.com",  // for HTTP only
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
public class YourTool : MorganaTool
{
    public YourTool(
        ILogger<MorganaAgent> logger,
        Func<MorganaContextProvider> getContextProvider) 
        : base(logger, getContextProvider) { }

    public async Task<string> YourMethod(string param1, int param2 = 5)
    {
        // Access context when needed
        var context = getContextProvider();
        var userId = context.GetContextVariable("userId");
        
        // Implementation
    }
}

// Register with adapter in AgentAdapter
toolAdapter.AddTool("YourMethod", yourTool.YourMethod, toolDefinition);

// Create AIFunction for agent
AIFunction function = toolAdapter.CreateFunction("YourMethod");
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

**MCP Tool Loading:**
For agents with `[UsesMCPServers]`, the system additionally:
1. Queries `IMCPServerRegistryService` for server names
2. Connects to InProcess or HTTP MCP servers
3. Loads `MCPToolDefinition` schemas from each server
4. Converts to `AIFunction` instances via `IMCPToolProvider`
5. Merges with native tools before agent creation

## Getting Started

### Prerequisites
- .NET 10 SDK
- Azure OpenAI Service account OR Anthropic API key

### Quick Start

1. **Clone the repository:**
```bash
git clone https://github.com/yourusername/morgana.git
cd morgana
```

2. **Configure your LLM provider in `appsettings.json`:**
```json
{
  "LLM": {
    "Provider": "AzureOpenAI",
    "AzureOpenAI": {
      "Endpoint": "https://your-resource.openai.azure.com/",
      "ApiKey": "your-api-key",
      "DeploymentName": "gpt-4"
    }
  }
}
```

3. **Define your domain-specific agents:**
```csharp
[MorganaAgent(Intent = "CustomerSupport")]
public class CustomerSupportAgent : MorganaAgent
{
    protected override async Task RegisterToolsAsync()
    {
        // Register your tools
    }
}
```

4. **Add your prompts to `prompts.json`:**
```json
{
  "Agents": [
    {
      "Name": "CustomerSupportAgent",
      "Intent": "CustomerSupport",
      "SystemPrompt": "You are a helpful customer support agent..."
    }
  ]
}
```

5. **Run the application:**
```bash
dotnet run --project Cauldron
```

The Blazor UI will be available at `https://localhost:5001`.

### Example Implementation

Check out the **Morgana.AI.Examples** project for a complete reference implementation including:
- Sample agents (Billing, Contract, Troubleshooting)
- Domain-specific tools and MCP servers
- Configured prompts and personalities
- Integration tests

This example demonstrates how to build a domain-specific conversational AI system on top of the Morgana framework.

## Use Cases

Morgana's domain-agnostic design enables a wide range of applications:

**Customer Service:**
- Multi-channel support automation
- Intelligent ticket routing and escalation
- Knowledge base integration

**Healthcare:**
- Patient intake and triage
- Appointment scheduling
- Medical record retrieval

**Finance:**
- Account inquiry handling
- Transaction dispute resolution
- Investment advisory

**Education:**
- Personalized tutoring
- Course recommendation
- Administrative assistance

**Enterprise:**
- HR policy guidance
- IT helpdesk automation
- Internal knowledge search

...

Simply implement domain-specific agents and tools—Morgana handles the orchestration, context management and conversational flow.

---

**Built with ❤️ using .NET 10, Akka.NET, Microsoft.Agents.AI and Model Context Protocol**

---

Morgana is developed in **Italy/Milan 🇮🇹**: we apologize if you find prompts and some code comments in Italian...but we invite you **to fly on the broomstick with Morgana 🧙‍♀️**
