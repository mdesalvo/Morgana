<table style="border:none;">
  <tr>
    <td width="256">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="256"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A multi-agent conversational system for enterprise customer service</strong></p>
      <p>
        <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10"/>
        <img src="https://img.shields.io/badge/Akka.NET-1.5-00ADD8?logo=akka" alt="Akka.NET"/>
      </p>
    </td>
  </tr>
</table>

## Overview

Morgana is an advanced conversational AI system designed to handle complex customer service scenarios through a sophisticated multi-agent architecture. Built on cutting-edge .NET 10 and leveraging the actor model via Akka.NET, Morgana orchestrates specialized AI agents that collaborate to understand, classify, and resolve customer inquiries with precision and context awareness.

The system is powered by Microsoft's Agent Framework, enabling seamless integration with Large Language Models (LLMs) while maintaining strict governance through guard rails and policy enforcement.

## Core Philosophy

Traditional chatbot systems often struggle with complexity—they either become monolithic and unmaintainable, or they lack the contextual awareness needed for nuanced customer interactions. Morgana addresses these challenges through:

1. **Agent Specialization**: Each agent has a single, well-defined responsibility with access to specific tools
2. **Actor-Based Concurrency**: Akka.NET provides fault tolerance, message-driven architecture, and natural scalability
3. **Intelligent Routing**: Requests are classified and routed to the most appropriate specialist agent
4. **Policy Enforcement**: A dedicated guard agent ensures all interactions comply with business rules and brand guidelines
5. **Full Observability**: Every conversation is archived with rich metadata for compliance, analytics, and continuous improvement

## Architecture

### High-Level Component Flow

```
┌────────────────────────────────────────────────────────────────┐
│                         User Request                           │
└──────────────────────────────┬─────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────┐
│                  ConversationManagerAgent                      │
│ (Coordinates, routes and manages stateful conversational flow) │
└──────────────────────────────┬─────────────────────────────────┘
                               │
                               ▼
┌────────────────────────────────────────────────────────────────┐
│               ConversationSupervisorAgent                      │
│  (Orchestrates the entire multi-turn conversation lifecycle)   │
└───┬───────────┬──────────────┬────────────────┬────────────────┘
    │           │              │                │        
    ▼           ▼              ▼                ▼        
┌───────┐  ┌──────────┐  ┌───────────┐  ┌──────────────┐
│ Guard │  │Classifier│  │Information│  │ Dispositive  │
│ Agent │  │  Agent   │  │   Agent   │  │    Agent     │
└───────┘  └──────────┘  └───────────┘  └──────────────┘
               │               │                │
               │               ▼                ▼
               │         ┌──────────┐     ┌────────────┐
               │         │ Billing  │     │ Contract   │
               │         │ Executor │     │Cancellation│
               │         └──────────┘     │ Executor   │
               │         ┌──────────┐     └────────────┘
               │         │Hardware  │
               │         │Troublesh.│
               │         │ Executor │
               │         └──────────┘
               │
               ▼
        ┌──────────────────┐
        │      LLM         │
        │ (Classification) │
        └──────────────────┘
```

### Agent Hierarchy

#### 1. ConversationManagerAgent
Coordinates and owns the lifecycle of a single user conversation session.

**Responsibilities:**  
- Creates and supervises the `ConversationSupervisorAgent` for the associated conversation.  
- Acts as the stable entry point for all user messages belonging to one conversation ID.  
- Forwards user messages to the supervisor and returns structured responses via SignalR.  
- Ensures that each conversation maintains isolation and state continuity across requests.  
- Terminates or resets session actors upon explicit user request or system shutdown.

**Key Characteristics:**  
- One `ConversationManagerAgent` per conversation/session.  
- Persists for the entire life of the conversation unless explicitly terminated.  
- Prevents accidental re-creation of supervisors or cross-session contamination.

#### 2. **ConversationSupervisor**
The orchestrator that manages the entire conversation lifecycle. It coordinates all child agents and ensures proper message flow.

**Responsibilities:**
- Receives incoming user messages
- Coordinates guard checks before processing
- Routes classification requests
- Delegates to appropriate coordinator agents
- Triggers conversation archival
- Handles error recovery and timeout scenarios

#### 3. **GuardAgent**
A policy enforcement agent that validates every user message against business rules, brand guidelines, and safety policies.

**Capabilities:**
- Profanity and inappropriate content detection
- Spam and phishing attempt identification
- Brand tone compliance verification
- Real-time intervention when violations occur
- LLM-powered contextual policy checks

**Example Guard Rules:**
```csharp
- Prohibited terms blocking
- Sentiment analysis for abusive language
- Request pattern analysis for spam detection
- Compliance with regional regulations
```

#### 4. **ClassifierAgent**
An intelligent routing agent that analyzes user intent and determines the appropriate handling path.

**Classification Categories:**
- **Informative**: User seeks information (billing details, contract status, technical specs)
- **Dispositive**: User requests action (contract cancellation, service modification, complaint filing)

**Intent Recognition:**
The classifier identifies specific intents such as:
- `billing_retrieval`: Fetch invoices or payment history
- `hardware_troubleshooting`: Diagnose connectivity or device issues
- `contract_cancellation`: Initiate service termination
- `other`: General service inquiries

**Metadata Enrichment:**
Each classification includes confidence scores and contextual metadata that downstream agents can use for decision-making.

#### 5. **InformativeAgent & DispositiveAgent**
Coordinator agents that maintain mappings of intents to specialized executor agents.

These agents act as smart routers:
```csharp
InformativeAgent:
  - billing_retrieval → BillingExecutorAgent
  - hardware_troubleshooting → HardwareTroubleshootingExecutorAgent

DispositiveAgent:
  - contract_cancellation → ContractCancellationExecutorAgent
```

#### 6. **Executor Agents**
Specialized agents with domain-specific knowledge and tool access.

**BillingExecutorAgent**
- **Tools**: `GetInvoices()`, `GetInvoiceDetails()`
- **Purpose**: Handle all billing inquiries, payment verification, and invoice retrieval
- **Example**: "Show me my last 3 invoices" → Retrieves from storage → Presents formatted data

**HardwareTroubleshootingExecutorAgent**
- **Tools**: `RunDiagnostics()`, `GetTroubleshootingGuide()`
- **Purpose**: Diagnose connectivity issues, provide step-by-step troubleshooting
- **Example**: "My internet is slow" → Runs diagnostics → Suggests solutions

**ContractCancellationExecutorAgent**
- **Tools**: `GetContractDetails()`, `InitiateCancellation()`
- **Purpose**: Handle contract modifications and termination requests
- **Example**: "I want to cancel my service" → Explains process → Initiates formal cancellation

## Technology Stack

### Core Framework
- **.NET 10**: Leveraging the latest C# features, performance improvements, and native AOT capabilities
- **ASP.NET Core Web API**: RESTful interface for client interactions
- **Akka.NET 1.5**: Actor-based concurrency model for resilient, distributed agent orchestration

### AI & Agent Framework
- **Microsoft.Extensions.AI**: Unified abstraction over chat completions with `IChatClient` interface
- **Microsoft Agent Framework**: Declarative agent definition with built-in tool calling support
- **Azure OpenAI Service**: GPT-4 powered language understanding and generation

### Tool & Function Calling
Tools are defined as C# methods with descriptive attributes that LLMs can discover and invoke:

```csharp
[Description("Retrieves user invoices for a specified period")]
public async Task<string> GetInvoices(
    [Description("User ID")] string userId,
    [Description("Number of recent invoices to retrieve")] int count = 3)
{
    // Implementation
}
```

The Agent Framework automatically:
1. Exposes tool schemas to the LLM
2. Handles parameter validation and type conversion
3. Invokes the appropriate method
4. Returns results to the LLM for natural language synthesis

The LLM retains context across turns, enabling natural follow-up questions.

**Built with ❤️ using .NET 10, Akka.NET and Microsoft.Agents**
