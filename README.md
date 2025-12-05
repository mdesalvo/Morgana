<table style="border:none;">
  <tr>
    <td width="144">
      <img src="https://github.com/mdesalvo/Morgana/blob/master/Morgana.jpg" alt="Morgana Logo" width="128"/>
    </td>
    <td>
      <h1>Morgana</h1>
      <p><strong>A Multi-Agent Conversational System for Enterprise Customer Service</strong></p>
      <p>
        <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet" alt=".NET 10"/>
        <img src="https://img.shields.io/badge/Akka.NET-1.5-00ADD8?logo=akka" alt="Akka.NET"/>
        <img src="https://img.shields.io/badge/Azure-Cloud%20Native-0078D4?logo=microsoft-azure" alt="Azure"/>
      </p>
    </td>
  </tr>
</table>

## Overview

Morgana is an advanced conversational AI system designed to handle complex customer service scenarios through a sophisticated multi-agent architecture. Built on cutting-edge .NET 10 and leveraging the actor model via Akka.NET, Morgana orchestrates specialized AI agents that collaborate to understand, classify, and resolve customer inquiries with precision and context awareness.

The system is powered by Microsoft's Agent Framework, enabling seamless integration with Large Language Models (LLMs) while maintaining strict governance through guard rails, policy enforcement, and comprehensive conversation archival.

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
┌─────────────────────────────────────────────────────────────────────┐
│                         User Request                                │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    ConversationSupervisor                           │
│  (Orchestrates the entire conversation lifecycle)                   │
└───┬───────────┬──────────────┬────────────────┬─────────────────┬───┘
    │           │              │                │                 │
    ▼           ▼              ▼                ▼                 ▼
┌───────┐  ┌──────────┐  ┌───────────┐  ┌──────────────┐   ┌──────────┐
│ Guard │  │Classifier│  │Information│  │ Disposition  │   │ Archiver │
│ Agent │  │  Agent   │  │   Agent   │  │    Agent     │   │  Agent   │
└───────┘  └──────────┘  └───────────┘  └──────────────┘   └──────────┘
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

#### 1. **ConversationSupervisor**
The orchestrator that manages the entire conversation lifecycle. It coordinates all child agents and ensures proper message flow.

**Responsibilities:**
- Receives incoming user messages
- Coordinates guard checks before processing
- Routes classification requests
- Delegates to appropriate coordinator agents
- Triggers conversation archival
- Handles error recovery and timeout scenarios

#### 2. **GuardAgent**
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

#### 3. **ClassifierAgent**
An intelligent routing agent that analyzes user intent and determines the appropriate handling path.

**Classification Categories:**
- **Informative**: User seeks information (billing details, contract status, technical specs)
- **Dispositive**: User requests action (contract cancellation, service modification, complaint filing)

**Intent Recognition:**
The classifier identifies specific intents such as:
- `billing_retrieval`: Fetch invoices or payment history
- `hardware_troubleshooting`: Diagnose connectivity or device issues
- `contract_cancellation`: Initiate service termination
- `contract_info`: Query contract terms and conditions
- `service_info`: General service inquiries

**Metadata Enrichment:**
Each classification includes confidence scores and contextual metadata that downstream agents can use for decision-making.

#### 4. **InformativeAgent & DispositiveAgent**
Coordinator agents that maintain mappings of intents to specialized executor agents.

These agents act as smart routers:
```csharp
InformativeAgent:
  - billing_retrieval → BillingExecutorAgent
  - hardware_troubleshooting → HardwareTroubleshootingExecutorAgent

DispositiveAgent:
  - contract_cancellation → ContractCancellationExecutorAgent
```

#### 5. **Executor Agents**
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

#### 6. **ArchiverAgent**
A persistence agent that records every conversation turn with rich metadata.

**Stored Data:**
- User messages and bot responses
- Classification results (category + intent)
- Session and user identifiers
- Timestamps for audit trails
- Sentiment and satisfaction indicators (future enhancement)

**Storage Backend:**
- Azure Table Storage for structured conversation logs
- Azure Blob Storage for attachments or large payloads
- Partitioned by session ID for efficient retrieval

## Technology Stack

### Core Framework
- **.NET 10**: Leveraging the latest C# features, performance improvements, and native AOT capabilities
- **ASP.NET Core Web API**: RESTful interface for client interactions
- **Akka.NET 1.5**: Actor-based concurrency model for resilient, distributed agent orchestration

### AI & Agent Framework
- **Microsoft.Extensions.AI**: Unified abstraction over chat completions with `IChatClient` interface
- **Microsoft Agent Framework**: Declarative agent definition with built-in tool calling support
- **Azure OpenAI Service**: GPT-4 powered language understanding and generation

### Cloud & Infrastructure
- **Azure Table Storage**: Structured conversation logs with millisecond query performance
- **Azure Blob Storage**: Large payload storage (documents, images, attachments)
- **Azure Application Insights**: Distributed tracing, performance monitoring, and diagnostics
- **Azure Identity**: Managed identity for secure, credential-free service access

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

## Key Features

### 1. **Intelligent Request Classification**
Every user message is analyzed by a specialized classifier that:
- Understands intent through semantic analysis
- Distinguishes between information requests and action requests
- Enriches requests with metadata for downstream agents
- Maintains classification confidence scores

### 2. **Specialized Agent Execution**
Each domain (billing, troubleshooting, contracts) has a dedicated executor with:
- Domain-specific system prompts
- Access to relevant tools and APIs
- Contextual conversation history
- Tailored response formatting

### 3. **Real-Time Policy Enforcement**
The GuardAgent runs on every message to:
- Block inappropriate or harmful content
- Ensure brand voice consistency
- Detect and prevent abuse patterns
- Comply with regulatory requirements

### 4. **Comprehensive Conversation Archival**
Every interaction is permanently stored with:
- Full message history
- Classification metadata
- Timestamp and session tracking
- User and agent identifiers
- Enables compliance audits, quality assurance, and ML training data collection

### 5. **Multi-Turn Conversation Support**
Agents maintain conversation threads via `AgentThread`:
```csharp
var thread = agent.GetNewThread();
var response1 = await agent.RunAsync("What's my balance?", thread: thread);
var response2 = await agent.RunAsync("When is it due?", thread: thread);
```
The LLM retains context across turns, enabling natural follow-up questions.

### 6. **Fault Tolerance & Resilience**
Akka.NET provides:
- **Supervision strategies**: Parent actors can restart failed children
- **Message buffering**: Messages aren't lost during actor restarts
- **Circuit breakers**: Prevents cascade failures to external services
- **Backpressure handling**: Graceful degradation under load

## Configuration

### appsettings.json
```json
{
  "Azure": {
    "StorageConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net",
    "AppInsightsConnectionString": "InstrumentationKey=...;IngestionEndpoint=https://...;LiveEndpoint=https://...",
    "OpenAI": {
      "Endpoint": "https://YOUR-RESOURCE.openai.azure.com",
      "DeploymentName": "gpt-4o-mini"
    }
  }
}
```

### Environment Variables (Production)
For security, use managed identities and Key Vault:
```bash
AZURE_CLIENT_ID=<managed-identity-client-id>
AZURE_TENANT_ID=<tenant-id>
AZURE_KEYVAULT_URI=https://your-keyvault.vault.azure.net/
```

## API Reference

### POST /api/conversation/message
Send a user message to Morgana.

**Request:**
```json
{
  "userId": "user-12345",
  "sessionId": "session-abc-xyz",
  "message": "What were my charges last month?"
}
```

**Response:**
```json
{
  "response": "Your November 2024 invoice totaled €150.00, due December 15, 2024. This includes your Premium 100Mbps plan and no additional charges.",
  "classification": "informative",
  "metadata": {
    "confidence": "0.98",
    "intent": "billing_retrieval"
  }
}
```

## Deployment

### Local Development
```bash
dotnet restore
dotnet build
dotnet run --project src/Morgana
```

### Docker Container
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Morgana.dll"]
```

### Azure App Service
```bash
az webapp create --resource-group morgana-rg --plan morgana-plan --name morgana --runtime "DOTNET:10"
az webapp config appsettings set --resource-group morgana-rg --name morgana --settings @appsettings.json
az webapp deployment source config-zip --resource-group morgana-rg --name morgana --src publish.zip
```

### Kubernetes (AKS)
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: morgana
spec:
  replicas: 3
  selector:
    matchLabels:
      app: morgana
  template:
    metadata:
      labels:
        app: morgana
    spec:
      containers:
      - name: morgana
        image: youracr.azurecr.io/morgana:latest
        ports:
        - containerPort: 80
        env:
        - name: AZURE_CLIENT_ID
          valueFrom:
            secretKeyRef:
              name: azure-identity
              key: client-id
```

## Extensibility

### Adding New Executors
1. **Create Tool Class:**
```csharp
public class NewDomainTool
{
    [Description("Performs specific domain operation")]
    public async Task<string> DoSomething([Description("Parameter")] string input)
    {
        // Implementation
    }
}
```

2. **Create Executor Agent:**
```csharp
public class NewDomainExecutorAgent : ReceiveActor
{
    private readonly AIAgent _agent;

    public NewDomainExecutorAgent(ILlmService llmService)
    {
        var adapter = new AgentExecutorAdapter(llmService.GetChatClient());
        _agent = adapter.CreateNewDomainAgent();
        ReceiveAsync<ExecuteRequest>(Execute);
    }
}
```

3. **Register in Coordinator:**
```csharp
_executors["new_intent"] = Context.ActorOf(resolver.Props<NewDomainExecutorAgent>(), "new-executor");
```

### Custom Guard Policies
Extend `GuardAgent` with custom rules:
```csharp
private async Task<bool> CheckCustomPolicy(string message)
{
    // Implement domain-specific validation
    if (message.Contains("sensitive_term"))
        return false;
    
    return await _llmService.CompleteAsync($"Is this compliant? {message}");
}
```

## Performance Considerations

### Akka.NET Actor Pool
For high-throughput scenarios, use router patterns:
```csharp
var props = Props.Create<BillingExecutorAgent>().WithRouter(new RoundRobinPool(10));
```

### LLM Call Optimization
- **Caching**: Cache classification results for similar queries
- **Batching**: Group multiple tool calls in single LLM request
- **Streaming**: Use `RunStreamAsync()` for faster perceived response times

### Storage Partitioning
Partition Azure Table Storage by:
- **Session ID**: Efficient single-session retrieval
- **User ID + Date**: Aggregate user history queries
- **Intent**: Analytics on request distribution

**Built with ❤️ using .NET 10, Akka.NET, and Microsoft Agent Framework**
