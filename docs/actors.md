---
title: "🎭 Actors"
permalink: /actors/
eyebrow: Pillar 1 of 6
tagline: Resilient multi-channel orchestration through Akka.NET message-driven architecture
lede: Every conversation gets its own little band of familiars. Let me follow one message with you, from the moment the customer presses Enter to my answer on their screen.
---

## My familiars

When a customer opens a conversation with me, I do not hand it to one big program. I call up a small band of **familiars** that serve that conversation and no other. Each has one job and they pass the work along by sending each other messages. Programmers call this the **actor model** and my familiars are built with the [Akka.NET](https://getakka.net) library.

You never write a familiar. What matters is what they give you:

- **One conversation never hurts another.** A customer waiting on a slow answer slows nobody else down.
- **Nothing is lost when I restart.** I call the familiars back from what I stored and the conversation carries on where it was.
- **Every step can be watched.** Each familiar reports its own step to your monitoring tools, so you can see where time and dust go.

## The journey of one message

A customer of my shop writes *"show me my invoices"*. This is what my familiars do:

<div class="flow across">
  <div class="step"><b>The keeper</b><span>receives the message</span></div>
  <div class="arrow"></div>
  <div class="step"><b>The guard</b><span>checks it is honest</span></div>
  <div class="arrow"></div>
  <div class="step"><b>The diviner</b><span>sees it is about billing</span></div>
  <div class="arrow"></div>
  <div class="step"><b>The guide</b><span>wakes the right witch</span></div>
  <div class="arrow"></div>
  <div class="step you"><b>Your witch</b><span>casts her spells and answers</span></div>
</div>

<ol class="steps" markdown="1">
<li markdown="1">

**The keeper receives it.** In code it is the `ConversationManager`: it knows which mirror the customer is looking into and keeps the conversation alive. Beside it the `ConversationSupervisor` leads the journey step by step.

</li>
<li markdown="1">

**The guard reads it.** My `Guard` asks an AI model whether the message is acceptable: no abuse and no attempt to trick me into misbehaving. A message that fails gets a polite refusal and goes no further.

</li>
<li markdown="1">

**The diviner understands it.** My `Classifier` reads the message against the subjects my coven covers and picks the best match. Here it is *billing*.

</li>
<li markdown="1">

**The guide hands it over.** My `Router` wakes the witch who looks after billing in this conversation.

</li>
<li markdown="1">

**Your witch answers.** She reads her tome, casts the spells she needs (here, reading the customer's invoices) and writes the answer. It appears on the screen word by word as she writes it.

</li>
</ol>

The guard and the diviner do quick, cheap work. They use the smallest of your three AI models, which you choose with one setting at the end of this page.

## Four moments worth knowing

**The witch stays.** Once my accounts witch has the conversation, the customer's next messages go straight to her and the diviner rests. When she says her work is done, I add a short closing line of my own and the next message is divined again.

{% include shot.html src="cauldron/agent-landing.jpg" caption="My accounts witch has the conversation. The header says who is speaking" %}

**A button needs no divining.** When the customer presses one of my own buttons, such as *Invoices* on my welcome, the choice is already clear. The message goes straight to that witch.

**Two subjects too close to tell apart.** When a message could belong to two witches almost equally, I do not guess. I ask the customer which one they meant, with a button for each.

**Nobody's subject.** When no witch of my coven covers the question, that is no error. I answer myself, say what I can help with and show a button for every subject.

## Patient, not endless

A good answer can take time, above all when a witch casts several spells. So I set no deadline for the whole answer. I wait as long as she keeps producing something and give up only after a long **silence**. Every piece of answer resets my patience, so a slow but busy witch is never cut short.

## What you may adjust

You will rarely need to. These live in the `Morgana:ActorSystem` section. [Your choices]({{ '/install/' | relative_url }}) shows where settings go.

| Setting | What it is for | My default |
|---|---|---|
| `Tier` | Which of your three AI models my guard, my diviner and my own words run on | `Economy` |
| `TimeoutSeconds` | How long a silence I tolerate before I give up on an answer | 180 |
| `IntentCollisionThreshold` | How close two subjects may score before I ask the customer to choose. 0.10 means ten points out of a hundred | 0.10 |
| `EnableUserGuardrail` | Switches my guard over customers' messages on or off | on |

My other guards and my limits are in [Safety and limits]({{ '/safety/' | relative_url }}).
