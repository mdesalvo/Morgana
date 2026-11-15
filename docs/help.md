---
title: Stuck? Start here
permalink: /help/
eyebrow: Start
lede: Lost in the manual or in a deployment? Find your situation below. Each row ends with the one page that settles it.
---

<div class="pills"><span class="pill ok">Press / anywhere to search the whole manual</span></div>

## Three quick wins

If you have ten minutes and want to be sure Morgana is for you, do these in order.

<ol class="steps" markdown="1">
<li markdown="1">
**See her work**
Start the stack and say *"Show me my invoices"*. Watch her ask for the customer code once and reuse it for the care plan. [Quick start]({{ '/quick-start/' | relative_url }})
</li>
<li markdown="1">
**See one agent as prose**
Open `Examples/agents.json` and read the Billing entry. That paragraph is the whole agent. [Prompts]({{ '/morgana/pillars/prompts/' | relative_url }})
</li>
<li markdown="1">
**See your own domain appear**
Run Alembic and answer its first question about your business. [Alembic]({{ '/alembic/' | relative_url }})
</li>
</ol>

## I want to...

| I want to | Go to |
|---|---|
| Understand what Morgana is in five minutes | [Welcome]({{ '/' | relative_url }}) then [At a glance]({{ '/morgana/' | relative_url }}) |
| Run the demo | [Quick start]({{ '/quick-start/' | relative_url }}) |
| Know what I must set before going live | [On your side: deployment]({{ '/morgana/configuration/deployment/' | relative_url }}) |
| Know what already works without touching it | [In the box]({{ '/morgana/configuration/in-the-box/' | relative_url }}) |
| Teach Morgana my business | [Alembic]({{ '/alembic/' | relative_url }}) or [On your side: your domain]({{ '/morgana/configuration/domain/' | relative_url }}) |
| Connect an existing service as a tool | [MCP in the agents pillar]({{ '/morgana/pillars/agents/' | relative_url }}) |
| Let two agents or two installations cooperate | [Agent to agent]({{ '/morgana/operations/a2a/' | relative_url }}) |
| Put the chat on my website | [The Cauldron widget]({{ '/channels/cauldron/' | relative_url }}) |
| Use Slack or another client | [Build your own channel]({{ '/channels/build-your-own/' | relative_url }}) |
| Understand and budget what a conversation costs | [Dust, models and pricing]({{ '/morgana/configuration/dust-and-models/' | relative_url }}) |
| Limit abuse | [Guard rails and limits]({{ '/morgana/operations/guard-rails/' | relative_url }}) |
| Watch what happens in production | [Observability]({{ '/morgana/operations/observability/' | relative_url }}) |
| Check my prompts did not regress | [PromptHarness]({{ '/prompt-harness/' | relative_url }}) |
| Look up a key | [Full key reference]({{ '/morgana/configuration/reference/' | relative_url }}) |
| Look up a word | [Glossary]({{ '/glossary/' | relative_url }}) |

## When something goes wrong

| What you see | Usually means | Do this |
|---|---|---|
| Morgana refuses to start and names a value | A placeholder (`_SECURE_OVERRIDE_` or `_FUNCTIONAL_OVERRIDE_`) is still in a tier or a key | Fill it. [Startup validation]({{ '/morgana/operations/startup-validation/' | relative_url }}) |
| Startup says an intent has no agent | You added it to `agents.json` and forgot the class | Add the agent class. [Your domain]({{ '/morgana/configuration/domain/' | relative_url }}) |
| Startup refuses a partner | An admitted partner has no `RateLimiting` block | Add one or write `"Enabled": false`. [Agent to agent]({{ '/morgana/operations/a2a/' | relative_url }}) |
| A terminal channel starts and Morgana cannot answer it | The channel's name does not resolve inside Docker | Run it with `--use-aliases`. [Docker]({{ '/morgana/operations/docker/' | relative_url }}) |
| The user gets a "slow down" message | The rate limit is 5 messages a minute per conversation | Raise it in `RateLimiting`. [Guard rails]({{ '/morgana/operations/guard-rails/' | relative_url }}) |
| The conversation says the dust has run out | The budget per conversation is spent | Start a new one or raise `BudgetPerConversation`. [Guard rails]({{ '/morgana/operations/guard-rails/' | relative_url }}) |
| Old conversations cannot be read after a redeploy | The encryption key changed | Restore the original key. [Deployment]({{ '/morgana/configuration/deployment/' | relative_url }}) |
| My site shows no widget | Its origin is not allowed to frame Cauldron | List it in `Widget:AllowedEmbedOrigins`. [Cauldron]({{ '/channels/cauldron/' | relative_url }}) |
| Morgana asks twice for something she already knows | The parameter is not shared or two instructions disagree | Check its scope and run the context group. [Context]({{ '/morgana/pillars/context/' | relative_url }}) |
| A request lands on the wrong agent | Two intent descriptions overlap | Sharpen the descriptions. [Your domain]({{ '/morgana/configuration/domain/' | relative_url }}) |
| An agent behaves oddly on one model only | The tier cannot hold a dense prompt | Try a more capable tier before rewriting prose. [Prompts]({{ '/morgana/pillars/prompts/' | relative_url }}) |
| A card shows raw `**` | A channel rendering bug | It cannot be fixed from a prompt. [Channels]({{ '/morgana/pillars/channels/' | relative_url }}) |
| Alembic fails at its first question | A tier is missing from the shared configuration | Declare all three tiers. [Deployment]({{ '/morgana/configuration/deployment/' | relative_url }}) |

## Still not found

Open an issue on [GitHub](https://github.com/mdesalvo/Morgana/issues) and say which page you expected the answer on. A manual that sends you hunting is a defect in the manual.
