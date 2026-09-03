# Morgana

Morgana is a modern and flexible **conversational AI framework** designed to handle complex scenarios
through a sophisticated **multi-agent, intent-driven architecture**. Built on cutting-edge **.NET 10**
and leveraging the actor model via **Akka.NET**, Morgana orchestrates specialized **AI agents** that
collaborate to understand, classify and resolve customer inquiries with precision and context awareness.

The system is powered by **Microsoft.Agents.AI**, enabling seamless integration with Large Language
Models (LLMs) while maintaining strict governance through guard rails and policy enforcement.

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
