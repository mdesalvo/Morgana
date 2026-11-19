---
title: "⚗️ Alembic"
permalink: /alembic/
banner: brand/alembic-banner.jpg
eyebrow: My companions
tagline: Yours the shape, mine the spells
lede: "My alembic is the still where your experts' knowledge becomes new tomes for my spellbook. Nobody writes a prompt by hand: I ask the questions and I distil the answers."
---

## What my still does

Your experts know their craft: how a refund is handled, what a contract covers, which customer needs what. They do not know how to write words for an AI and they should not have to.

So I interview them. In their own words, one question at a time, I find out which subjects your customers ask about and what each witch must know, do and never do. From the answers I distil a **complete plugin**: subjects, tomes, spells and working C# code, ready for my `plugins/` folder.

Three things to know about my still:

- **It needs only an AI model**, not a running Morgana. Your experts can use it whenever they like.
- **Nothing lives on its disk.** Work arrives as a file you upload and leaves as a file you download, so it runs just as well on a laptop or in a cloud.
- **It is me, too.** The interviewer is a witch of my own, with a tome written like yours, standing on my own character.

<div class="shots two">
{% include shot.html src="alembic/landing.jpg" caption="Two doors: distil a new world or take up one already begun" %}
{% include shot.html src="alembic/interview-map.jpg" caption="The interview opens on the map: which of your processes I will run. The grey sentence is an example from your own trade" %}
</div>

## The interview

A world has two halves: the **subjects** your customers ask about and one **witch** per subject. I settle them in that order.

<ol class="steps" markdown="1">
<li markdown="1">

**The map**, once. Every subject: its name, its description for my diviner, the label of its button and its opening sentence.

</li>
<li markdown="1">

**Each witch, chapter by chapter**: her purpose and its limits, her character (I offer, you choose), her spells with their ingredients, her territory for her sisters, her instructions and how she presents what her spells return.

</li>
<li markdown="1">

**Her acceptance.** I read the whole witch back to you and you let her into the coven. That decision is yours alone.

</li>
<li markdown="1">

**The sisters**, once at the end. For each witch, a box for every sister she may ask, here or in a faraway coven.

</li>
<li markdown="1">

**The checks**, if you want them. Then the archive.

</li>
</ol>

<div class="shots two">
{% include shot.html src="alembic/interview-follow-up.jpg" caption="Accepting my proposal: I reflect it back and ask only what is still missing" %}
{% include shot.html src="alembic/agents.jpg" caption="One witch in the making: each chapter on the track can be opened and read" %}
</div>

My manners during the interview:

- **One question, one step.** Whatever I can write down moves us forward. What I can guess I propose and you correct. Asking the same thing twice is how an interview fails.
- **The map is a choice, not an inventory.** Only the subjects you actually want me to handle become witches.
- **Your expert is not the witch's audience.** Your customers are. Every sentence I write describes those customers to the witch, in their words.

Anything already written can be reopened from the track and read, at no cost. When a witch is wrong, **Update** walks through her chapters again and every chapter that is still right is kept with one press.

## The checks

Before the archive you may ask me to check the work. There are two kinds:

- **Plain checks** repeat what I verify when I [wake up]({{ '/safety/#when-i-refuse-to-start' | relative_url }}): every subject has a witch, every spell has a description. Each finding leads you back to the chapter to fix.
- **Checks marked AI** read the witches side by side for what only shows between them, such as two territories that overlap. Each finding comes with a proposed fix you can apply.

The checks advise. They never block, because an expert who disagrees about their own craft is usually right.

<div class="shots two">
{% include shot.html src="alembic/domain.jpg" caption="The sisters: for each witch, a box for every sister she may ask" %}
{% include shot.html src="alembic/validation.jpg" caption="The checks you chose. The AI badge marks one I judged" %}
</div>

## My still has guards too

A tome is an order my witches obey without question. Whoever writes one with me holds power over your customers and my still keeps that power honest. The guards are the [same as at my doors]({{ '/safety/#why-i-need-guards' | relative_url }}), cut to the measure of an author:

- **What your expert tells me.** Any subject a business may lawfully deal in passes, however delicate: medicines, debts, weapons. A witch turned against the customers she serves does not pass, nor does an order written to the AI beneath her instead of about the work. I say what I caught and why. The answer stays in its box, so your expert removes that part and sends the rest.
- **What you bring back.** A file may have passed through other hands, so I read all of it before I take it in. One such passage is enough: I keep nothing of the file and name the witch and the chapter where it sits.
- **The stand-ins I write.** A stand-in may reach beyond itself only where its spell's description asks it to, such as saving the document it says it saves. Before it enters the archive I read it against that description. If it strays I write it again. If it strays twice there is no archive: the log names the spell and what it did, while a button takes you straight to that witch.

When the AI behind my guards does not answer, I stop and say so, as on any other step. Nothing unjudged goes forward.

## The archive

Last, I ask a few technical facts: the namespace, the class names, the AI level of each witch and the MCP servers she borrows from. Then everything is distilled into **one archive**, because the pieces only fit together.

{% include shot.html src="alembic/archive.jpg" caption="The archive card: the technical facts first, then one download" %}

| Inside | What it is |
|---|---|
| A project and a solution | Ready to build, referencing the same version of me the still runs on |
| `agents.json` | Your subjects and tomes |
| Two files per witch and per set of spells | One I write and rewrite, one that is yours forever |
| A working stand-in for every spell | You can talk to your witches on the first day and hear whether the tomes are right |
| A migration report | Always included, so your team learns to read it before the day it matters |

The two files are the key to coming back. The `.g.cs` file is mine: I overwrite it whenever you distil again. The `.cs` file is yours: I write it once with the stand-in and never touch it again. That is where your real integration goes.

## Coming back

My import page takes three kinds of file and tells them apart by reading them:

- an `agents.json` of a world already at work
- an `alembic-draft.json`: an interview left halfway
- an `alembic.zip`: an archive I wrote before

**Save my work** sits beside every card, so a closed tab never costs you an interview.

{% include shot.html src="alembic/import.jpg" caption="What I can do with what you bring back" %}

## Running the still

```bash
docker compose --env-file .env --env-file .env.versions --profile authoring up alembic   # http://localhost:5005
```

My still reads the same three AI levels as I do and works on **Efficiency**. Its whole job is writing words that never contradict each other, so it is no place to save. It runs once per world rather than on every customer's turn, but each interview step is a paid AI call.
