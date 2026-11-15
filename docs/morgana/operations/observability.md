---
title: Observability
permalink: /morgana/operations/observability/
eyebrow: Operating
lede: End-to-end tracing across the whole pipeline, meaningful to an operator reading latencies and to a business owner reading intents.
---

## One trace per turn

<pre class="mermaid">
graph TD
  T["morgana.turn"] --> G["morgana.guard"]
  T --> C["morgana.classifier"]
  T --> R["morgana.router"]
  R --> A["morgana.agent"]
  A --> K["morgana.consultation"]
</pre>

| Span | Key attributes |
|---|---|
| `morgana.turn` | `conversation.id` · `turn.user_message` |
| `morgana.guard` | `guard.compliant` · `guard.violation` · `guard.source`. `error.type` instead of a verdict when no guard could judge |
| `morgana.classifier` | `classification.intent` · `classification.confidence` |
| `morgana.router` | `router.intent` · `router.agent_path` |
| `morgana.agent` | `agent.name` · `agent.ttft_ms` · `agent.response_preview` · `agent.tools_invoked` |
| `morgana.consultation` | `consultation.caller` · `consultation.target` · `consultation.awaiting_reply`. Nested under the answering agent's work |

`agent.tools_invoked` carries tool **names** and never their arguments. An incoming HTTP trace context arrives as a link.

## Metrics

| Metric | Unit | Tags |
|---|---|---|
| `morgana.dust.consumed` | dust | `dust.llm_role` · `conversation.id` |
| `morgana.security.events` | events | `security.door` · `security.outcome` · `security.source`. `error.type` when a door could not judge |
| `morgana.guard.rejections` | messages | Messages refused on a judgment |
| `gen_ai.usage.*` | tokens | Emitted by Microsoft.Extensions.AI beside the above so cost in dust and cost in tokens read on one dashboard |

`morgana.security.events` counts what happens at every guarded door, a fail-closed one included. It never carries a tool name, because an MCP server chooses its own names.

## Where it goes

```json
"OpenTelemetry": {
  "Enabled": true,
  "ServiceName": "Morgana",
  "EnableSensitiveData": false,
  "Exporters": [
    { "Name": "console", "Enabled": true },
    { "Name": "otlp", "Enabled": true, "Endpoint": "http://localhost:4317" }
  ]
}
```

Any OTLP backend works: Jaeger, Grafana Tempo, Azure Monitor, Datadog and the rest. `EnableSensitiveData` puts message and response text on spans. It is off by default and should stay off wherever conversations hold personal data.
