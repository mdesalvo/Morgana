---
title: "6 · Authoring"
permalink: /morgana/pillars/authoring/
eyebrow: Pillar 6 of 6
lede: The hard part of building agents is not infrastructure. It is writing clear and consistent domain prose and the tool contracts behind it. Morgana gives that job its own workbench.
---

## Two routes to a domain

<div class="cards two" markdown="1">
<div class="card" markdown="1">

### By hand
Write `agents.json` and a few C# classes against the `Morgana.AI` NuGet package. Package them as a plugin DLL. This is the route for developers who know exactly what they want.

[See the domain checklist →]({{ '/morgana/configuration/domain/' | relative_url }})
</div>
<div class="card" markdown="1">

### With Alembic
An AI-led interview with a domain expert produces intents, agent prose, tool contracts and working C#. The expert never writes a prompt.

[Meet the workbench →]({{ '/alembic/' | relative_url }})
</div>
</div>

## Why a workbench

The people who understand a business are rarely the people who write plugins. Asking a domain expert to author instructions for a model invites two failures: prose that reads well to a human and means little to a model. The other is prose that quietly contradicts itself. Alembic reverses the roles. The expert answers questions about their own work in their own words. The workbench distils those answers into the dispositive sentences an agent needs.

<div class="shots">
{% include shot.html src="alembic/map.jpg" caption="The domain map: one intent per process Morgana is being installed to run" %}
{% include shot.html src="alembic/validation.jpg" caption="Validation: deterministic and AI-judged checks, each with a way to the fix" %}
</div>

## What comes out

One archive that you can build and drop into the `plugins/` folder of any Morgana:

- a ready `.csproj` and `.slnx` pinned to the Morgana build Alembic itself runs against
- `agents.json`, embedded as a resource
- generated agent and tool classes, split so a later re-run never overwrites your own code
- a working mock per toolkit, so the first conversation already sounds right
- an unconditional migration report

## Prose as a first-class artifact

Because behaviour lives in prompt configuration, a change to what an agent does is a change to a file. It is reviewed in a pull request and measured by the [PromptHarness]({{ '/prompt-harness/' | relative_url }}) like any other change, with no recompilation and no redeployment of application code.
