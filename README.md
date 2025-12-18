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

## Architecture

### High-Level Component Flow

```
┌────────────────────────────────────────────────────────────────┐
│                         User Request                           │
└──────────────────────────────┬─────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────┐
│                   ConversationManagerActor                     │
│ (Coordinates, routes and manages stateful conversational flow) │
└──────────────────────────────┬─────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────┐
│                  ConversationSupervisorActor                   │
│  (Orchestrates the entire multi-turn conversation lifecycle)   │
└───┬───────────┬───────────────┬────────────────────────────────┘
    │           │               │                        
    ▼           ▼               ▼                        
┌───────┐  ┌──────────┐   ┌───────────┐  
│ Guard │  │Classifier│   │   Router  │  
│ Actor │  │  Actor   │   │   Actor   │  
└───────┘  └──────────┘   └───────────┘  
    │           │               │        
    │           │               ▼        
    │           │         ┌───────────┐   
    │           │         │  Morgana  │   
    │           │         │   Agent   │   
    │           │         └───────────┘
    │           │               │____________________________...fully extensible intent-focused agents
    │           │               │              │            │
    │           │               ▼              ▼            ▼
    │           │         ┌──────────┐   ┌───────────┐  ┌──────────────────┐ * Built-in example agents
    │           │         │ Billing* │   │ Contract* │  │ Troubleshooting* │
    │           │         │  Agent   │   │   Agent   │  │      Agent       │
    │           │         │          │   │           │  │                  │
    │           │         └──────────┘   └───────────┘  └──────────────────┘
    │           │              │               │            │
    │           │              │               │            │
    └─────┬─────┘              └───────────────┬────────────┘
          │                                    │ 
          ▼                                    ▼
         ┌──────────────────────────────────────────────┐
         │                  ILLMService                 │
         │  (Guardrail, Intent Classification, Tooling) │
         └──────────────────────────────────────────────┘
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
- Triggers conversation archival
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
  - Additional properties (guard terms, error messages)
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
Type? agentType = agentRegistryService.GetAgentType("billing");
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
- **Microsoft.Agents.AI**: Declarative agent definition with built-in tool calling support
- **Azure OpenAI Service**: GPT-4 powered language understanding and generation

### Tool & Function Calling

Tools are defined as C# delegates mapped to tool definitions from prompts. The `ToolAdapter` provides runtime validation and dynamic function creation:

```csharp
// Define tool implementation
public async Task<string> GetInvoices(string userId, int count = 3)
{
    // Implementation
}

// Register with adapter
toolAdapter.AddTool("GetInvoices", billingTool.GetInvoices, toolDefinition);

// Create AIFunction for agent
AIFunction function = toolAdapter.CreateFunction("GetInvoices");
```

**Validation:**
- Parameter count and names must match between implementation and definition
- Required vs. optional parameters are validated
- Type mismatches are caught at registration time

The Agent Framework automatically:
1. Exposes tool schemas to the LLM
2. Handles parameter validation and type conversion
3. Invokes the appropriate method
4. Returns results to the LLM for natural language synthesis

## Conversational Memory

Each `MorganaAgent` maintains an in-memory conversation history for multi-turn context:

```csharp
protected readonly List<(string role, string text)> history = [];
```

**Interaction Token: `#INT#`**

Agents use a special token `#INT#` to signal when additional user input is required:
- Present: Conversation continues, awaiting more information
- Absent: Conversation completed, user may close or start new request

This enables natural back-and-forth dialogues within a single intent execution.

**Future Enhancement:**
The in-memory history is a temporary solution until the Agent Framework provides native memory support.

---

**Built with ❤️ using .NET 10, Akka.NET, Microsoft.Agents.AI and Microsoft.Extensions.AI**
