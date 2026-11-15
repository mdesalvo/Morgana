---
title: "✨ Authoring"
permalink: /authoring/
eyebrow: Pillar 6 of 6
tagline: An AI-driven workbench that distils domain expertise into production-ready agents
lede: How you replace my little shop with your own world. You can write your tomes yourself or let my alembic interview your experts and distil the tomes for you.
---

## What you are writing

Your world enters my grimoire as a **plugin**: a small .NET library I read when I wake up. It holds everything from the previous chapters and nothing else:

```text
MyDomain/
├── agents.json            the subjects and the tome of every witch
├── Agents/
│   └── BillingAgent.cs    the seal: subject and AI level
└── Tools/
    └── BillingTool.cs     the spells she can cast
```

You compile it and put the result in my `plugins/` folder. I find the witches by myself. Several plugins can sit side by side and I merge their subjects.

<div class="cards">
<div class="card"><span class="kind">For developers</span><h3>By your own hand</h3><p>For a developer who knows exactly what the witches should be. Five steps, just below.</p></div>
<a class="card" href="{{ '/alembic/' | relative_url }}"><span class="kind">For experts</span><h3>Through my alembic</h3><p>For an expert who knows the craft but not AI. Alembic asks the questions and writes the plugin.</p><span class="more">Meet Alembic →</span></a>
</div>

## By your own hand, in five steps

<ol class="steps" markdown="1">
<li markdown="1">

**Name the subjects.** Each subject your customers ask about becomes an intent in the `Intents` list of `agents.json`:

```json
{
  "Name": "billing",
  "Description": "requests to view the invoices the nursery issued to the customer, check what is still due or review payment history",
  "Label": "🧾 Invoices",
  "DefaultValue": "What can you do with my invoices?"
}
```

`Name` binds everything together. `Description` is what my diviner reads to recognise the subject, so make each one clearly different from the others. `Label` is the text on my button for it and `DefaultValue` is the sentence that button sends.

</li>
<li markdown="1">

**Write each witch's tome.** One entry in the `Agents` list per subject, with the five chapters described in [Prompts]({{ '/prompts/#the-five-chapters-of-a-witchs-tome' | relative_url }}).

</li>
<li markdown="1">

**Give each witch her seal.** A class with `[HandlesIntent]` and `[RequiresLLMTier]`, as in [Agents]({{ '/agents/#what-a-witch-is-made-of' | relative_url }}). Add `[ConsultsAgent]` for each sister she may ask and `[UsesMCPServer]` for each spell server she borrows from.

</li>
<li markdown="1">

**Write the spells,** where a witch must reach your systems. Every spell and every ingredient needs a description, because that is all the AI ever sees of it. A spell returns facts: what it found or what it did. It never tells the witch how to behave.

</li>
<li markdown="1">

**Build and bring it to me.** The project references the `Morgana.AI` package and embeds `agents.json` as a resource. Put the compiled output in `plugins/` and wake me.

</li>
</ol>

My shop, in the `Examples` folder of the repository, is a complete plugin written this way. It is the best one to copy from.

> When something does not fit together, I refuse to open my book and I say what is wrong: a subject without a witch, a spell without a description, two plugins claiming the same subject. A mistake costs you a restart, never a customer.
{: .tip}

## Through my alembic

Writing good tomes is the hard part. Those who know a craft best are rarely those who write words for AI. My alembic turns the roles around: it **interviews** your expert in their own words and distils the subjects, the tomes, the spells' definitions and the C# code.

<div class="shots two">
{% include shot.html src="alembic/map.jpg" caption="The map of subjects, drawn from the interview" %}
{% include shot.html src="alembic/validation.jpg" caption="My alembic checks the witches before they leave the still" %}
</div>

At the end you download one archive that builds into a plugin, ready for `plugins/`. It even carries a working stand-in for every spell, so you can talk to your witches before the real integration exists. [Alembic]({{ '/alembic/' | relative_url }}) shows the whole distillation.

## Prove before you trust

A tome has no compiler. A sentence can read well and still contradict another one. Before you rely on a change, take it to my [proving ground]({{ '/prompt-harness/' | relative_url }}): it replays real conversations against the real model and tells you whether my witches still behave.

## Deeper magic: replacing a piece of me

Everything I do is a separate service that can be replaced. A development team can swap any one of them for its own without touching my framework. The ones most often replaced:

| Service | What I bring | Replace it to |
|---|---|---|
| `IConversationPersistenceService` | Sealed files, one per conversation | Keep conversations in your own database |
| `IAuthenticationService` | Signed tokens, one key per mirror | Use the identity provider you already trust |
| `IGuardRailService` | My AI guards | Use a moderation service you already pay for |
| `IClassifierService` | My AI diviner | Route by your own rules |
| `IRateLimitService` · `IDustLimitService` | Counters inside each conversation's file | Share limits across several servers or bill from your own ledger |

Each one is registered in my startup code and replaced by registering yours in its place.
