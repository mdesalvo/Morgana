---
title: Alembic
permalink: /alembic/
banner: brand/alembic-banner.jpg
eyebrow: Companion · authoring
lede: Yours the shape and hers the spells. A functional interview with a domain expert, distilled into a complete and buildable Morgana domain. Nobody writes a prompt by hand.
---

<div class="pills"><span class="pill">Blazor Server</span><span class="pill">Fluent UI</span><span class="pill">Port 5005</span><span class="pill ok">No Morgana instance needed</span></div>

## What it is

Alembic is the authoring workbench of the project. A domain expert answers questions about their own business in their own words. Alembic turns the answers into **intents, agent prose, tool contracts and working C#**, ready to compile into a Morgana plugin.

Three properties define it:

- **It is not a channel.** It never calls a running Morgana and holds no token. Its only dependency is a language model
- **It is not a filesystem tool.** Configuration arrives as an upload and leaves as a download, so Alembic can live in a cloud or on a laptop
- **It is itself an agent of Morgana.** Its prompt is layered from an `alembic.json` of the same shape as a domain agent's, over Morgana's own personality. It runs under the same framework as the domains it authors

<div class="shots">
{% include shot.html src="alembic/landing.jpg" caption="Two doors: distil a new domain or resume one" %}
{% include shot.html src="alembic/interview-map.jpg" caption="The interview opens on the domain: which of the client's processes Morgana will run. The greyed sentence is a worked example for their trade" %}
{% include shot.html src="alembic/interview-follow-up.jpg" caption="Accepting the proposal reflects it back and asks only what is still missing" %}
</div>

## The interview

A domain has two halves: the **intents** customers can ask for and one **agent** per intent. The interview builds them in that order.

<pre class="mermaid">
graph LR
  M["Domain mapping<br/>once"] --> T["Target"]
  T --> P["Personality"]
  P --> K["Toolkit"]
  K --> R["Territory"]
  R --> I["Instructions"]
  I --> F["Formatting"]
  F --> A["Acceptance"]
  A -->|next agent| T
  A -->|last agent| C["Collaboration<br/>once"]
  C --> V["Validation"]
  V --> E["Morganize"]
</pre>

| Step | Runs | Settles |
|---|---|---|
| **Domain mapping** | once | Every intent: name, description, label and opening sentence |
| **Target** | per agent | What the agent does and what it must never do |
| **Personality** | per agent | Which facet of Morgana this agent is. Offered rather than dictated |
| **Toolkit** | per agent | The tools with their descriptions and parameters and scopes |
| **Territory** | per agent | What this desk is characteristically the one to be asked about |
| **Instructions** | per agent | Behavioural rules, written after the toolkit exists |
| **Formatting** | per agent | How the agent presents its own tools' output |
| **Acceptance** | per agent | No question: the agent is read back whole and the client lets it in |
| **Collaboration** | once | Which agent may consult which, here and at partner installations |

<div class="shots">
{% include shot.html src="alembic/map.jpg" caption="The domain map: each entry becomes one intent and one agent" %}
{% include shot.html src="alembic/agents.jpg" caption="One agent in hand: its seven moments open on the track and every earlier one can be read" %}
</div>

### Principles of the conversation

- **One question and one step.** An answer advances the step if anything can be written down. The rest is inferred and proposed and corrected rather than asked again. Circling is how an AI-led interview really fails
- **The map is a choice and not an inventory.** The intents that surface in the conversation make the map
- **The interviewee is not the agent's audience.** The expert never talks to the agent. Their customers do. Every sentence Alembic writes describes those customers to the agent
- **The client owns the final word.** Letting an agent into the domain at Acceptance is the one decision that is theirs alone

## Reading and correcting

Any moment behind the frontier can be opened from the track and read. Reading calls no model and writes nothing. Where an agent is wrong, **Update** reopens it at the Target and walks forward through every section. A section whose ground did not move is settled with one press and kept word for word.

## Colleagues and validation

**Collaboration** is settled once at the end on a form: for each agent a box per colleague it may ask, including agents on partner Morgana installations. A remote colleague becomes `[ConsultsAgent("intent", "partner")]` in the generated code and carries a partner badge. It is taken on trust: the partner's own card is what knows and it is read at the first consultation.

**Validation** is chosen and never imposed. Deterministic checks restate the rules Morgana enforces at startup, so their sentence arrives while a fix still costs nothing. Checks badged **AI** read the agents side by side for what only shows between them: overlapping territories and a colleague out of step with the prose that should offer it. A deterministic finding leads back to its section. A judged one carries a written fix you can apply. It advises and never blocks, because a domain expert who disagrees about their own business is usually right.

<div class="shots">
{% include shot.html src="alembic/domain.jpg" caption="Collaboration: for each agent a box per colleague it may consult" %}
{% include shot.html src="alembic/validation.jpg" caption="Validation: checks chosen by the client and never imposed. The AI badge marks a judged one" %}
</div>

## Morganize

The last step is the **Archive** card. It first asks for the technical facts of the code: namespace, class names, the LLM tier of each agent and the MCP servers it reaches. Then the domain distils into **one archive**, because the pieces are only correct together.

<div class="shots">
{% include shot.html src="alembic/archive.jpg" caption="The archive card: technical facts first and then one download" %}
</div>

| In the archive | Is |
|---|---|
| `.csproj` and `.slnx` | Referencing `Morgana.AI` and `Morgana.Contracts` pinned to the build Alembic itself runs against |
| `agents.json` | Embedded as a resource |
| Generated sources | One pair per agent and per toolkit |
| A working mock per toolkit | Not a stub: you can talk to your agent on the first run and hear whether the prose is right |
| A migration report | Unconditional, even for a greenfield domain, so everyone learns to read it before the day it matters |
| `README.md` · `MIGRATION.md` | The two-halves convention and the interview's own save file |

### The two halves

Each agent and toolkit is generated as two files so a re-run never clobbers your code.

| File | Owner | Rule |
|---|---|---|
| `AgentX.g.cs` | Alembic | Attributes and constructor and partial signatures. **Always overwritten** |
| `AgentX.cs` | You | The working mock first and your real integration later. **Written once and never touched again** |

A tool declared in the generated half and forgotten in yours does not compile. That is the point.

## Bringing a domain back

The import page takes three kinds of file and tells them apart by reading, never by name:

- `agents.json`: a domain already at work
- `alembic-draft.json`: a sitting left unfinished
- `alembic.zip`: an archive Alembic wrote

<div class="shots">
{% include shot.html src="alembic/import.jpg" caption="The import doors: go over the agents and carry on and validate and morganize" %}
</div>

**Save my work** sits beside every card and hands over the file at any point, so a closed tab costs nothing.

## Run it

```bash
docker compose --env-file .env --env-file .env.versions --profile authoring up alembic   # http://localhost:5005
cd Alembic && dotnet run                                                                   # from source
```

Alembic reads the same `Morgana:LanguageModels:Tiers` section as Morgana. Inside the repository it shares Morgana.Web's user secrets, so it runs against whatever the working copy is wired to. It does its model work on the **Efficiency** tier and never on Economy: its whole job is writing prose that does not contradict itself. It runs once at onboarding rather than on every turn. That is the wrong place to save.

| Key | Purpose |
|---|---|
| `Morgana:LanguageModels:Tiers` | The three tiers, as in Morgana |
| `Alembic:Work:AutosaveSeconds` | How often the draft is snapshotted as a floor under **Save my work**. Default 30 |

Every interview turn is a paid call, so keep an eye on what the tier costs.
