# Morgana

Morgana is a multi-agent, multi-channel **conversational AI framework** for .NET 10, built on the
actor model (Akka.NET) and Microsoft.Agents.AI. Domain experts model agents declaratively — prompt
and tools in JSON, a thin C# class — and the framework handles orchestration, streaming,
persistence, guard rails, channel adaptation and observability.

The source, the issue tracker and the releases live in the
[repository](https://github.com/mdesalvo/Morgana).

## Handbooks

<div class="handbooks">
  <a class="handbook" href="Morgana-Handbook.html">
    <div class="icon">&#x1F52E;</div>
    <h4>Morgana Handbook</h4>
    <p>The framework: architecture, pipeline, agent authoring, prompt composition, channels,
       persistence, observability.</p>
  </a>
  <a class="handbook" href="Alembic-Handbook.html">
    <div class="icon">&#x2697;&#xFE0F;</div>
    <h4>Alembic Handbook</h4>
    <p>The authoring workbench that distils an interview with a domain expert into a complete,
       buildable Morgana domain.</p>
  </a>
</div>

## Specifications

Morgana speaks open protocols, and where one leaves a gap the extension that fills it is published
here rather than kept private.

- [**A2A bearer issuance, v1**](a2a/extensions/bearer-issuance/v1/) — the A2A agent-card extension
  by which a published agent declares the JWT issuer and audience a caller must mint its bearer
  token under.
