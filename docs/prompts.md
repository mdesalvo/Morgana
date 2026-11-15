---
title: "📝 Prompts"
permalink: /prompts/
eyebrow: Pillar 3 of 6
tagline: First-class artifacts with layered personality architecture and structured behavioral policies
lede: "My witches are mostly words. Let me open my tomes for you: where the words live, how I brew them into the potion each witch drinks and the one rule that keeps the potion pure."
---

## My witches are prose, not code

An AI model does what its words tell it. My witches' words are not buried in a program: they are plain text in JSON files that anyone on your team can read, review and change. In code they are called **prompts**. Building or tuning a witch is mostly **writing clear sentences**.

The words come from two tomes:

| Tome | Whose | What it holds |
|---|---|---|
| `morgana.json` | Mine | My own character, the rules my whole coven follows and the words of my guard, my diviner and my greetings |
| `agents.json` | Yours | The subjects your customers can ask about and the tome of each of your witches |

You never touch my tome to write your world. Everything about yours goes in `agents.json`.

## The five chapters of a witch's tome

Each witch in `agents.json` is written in five short chapters. Here is my accounts witch, shortened:

```json
{
  "ID": "Billing",
  "Target": "You keep the accounts book of The Greenhouse & Nursery.",
  "Personality": "You are a formal and pragmatic witch, the one who keeps the nursery's accounts. Be exact with money: a garden is paid for in real coin.",
  "Instructions": "Your accounts tools are read-only. You can show invoices, what is still outstanding and payment history and nothing else...",
  "Formatting": "One card shape per tool. GetInvoices: one section per invoice...",
  "Territory": "The money side of this customer's dealings with the nursery: the invoices issued to them, what is still outstanding and the payments actually received."
}
```

| Chapter | Answers the question |
|---|---|
| `Target` | What is this witch for and where does her job stop? |
| `Personality` | Which side of me does she show? |
| `Instructions` | How does she go about her work and what must she never do? |
| `Formatting` | How does she present what her spells return: a card, a list, buttons? |
| `Territory` | What should her *sisters* come to her for? She never reads this chapter herself: it is how the others know when to ask her |

## The potion each witch drinks

Before a witch speaks, I brew her potion from three ingredients, always in the same order. My character comes first: kind, honest, a good witch. My rules follow. Her own tome comes last and adds only what is special about her.

<div class="flow across">
  <div class="step"><b>My character</b><span>the same for every witch</span></div>
  <div class="arrow"></div>
  <div class="step"><b>My rules</b><span>the same for every witch</span></div>
  <div class="arrow"></div>
  <div class="step you"><b>Her tome</b><span>only what is hers</span></div>
</div>

That is why my accounts witch can be formal and my contracts witch patient while a customer always feels they are talking to me.

## The rules of my coven

My tome carries nine **global policies**, rules I wrote once for my whole coven. They are why your tomes can stay short. In plain words, they make every witch:

1. look in what she already knows before asking the customer anything
2. use buttons in one way, always with a way out
3. say clearly when she is done and hand the conversation back
4. stay in the conversation until then
5. cast her spells properly
6. state only what her spells really returned, never inventing a figure
7. always answer with words, even when she also shows buttons or a card
8. use a card when the data has a shape
9. ask a sister when the question belongs to her, if she has sisters

## The one rule of writing

> **Say the least and the most specific.** A witch's tome says only what is particular to her. Whatever my rules already say is left out, because a second version of the same rule is a second voice that may one day disagree with the first.
{: .tip}

The typical curse of an AI assistant is not a crash. It is **two sentences that contradict each other**, often one in a tome and one among my rules. The model then follows one of them some of the time. The cure is to remove or rephrase, never to add a louder sentence. My [proving ground]({{ '/prompt-harness/' | relative_url }}) tells you whether a change helped.

Two more pieces of advice:

- **Write for the customer**, in their words. *"My roses are wilting"* is how a customer talks, so my garden witch is described around that and never around the shop's product codes.
- **No capitals for emphasis.** They do not make a model obey more. They make a tome harder to keep consistent.

## When the tome is right and the witch still errs

Rule these out before you touch a word:

- **The model is too small.** A rich, layered potion needs a capable model. A witch on `Economy` may fail where the same tome on `Efficiency` works.
- **The mirror draws it wrong.** A card showing stray `**` symbols is a fault of the channel's display, which no words can fix.

## A change is a change to a page

Because the words are data, changing what a witch says or does means editing `agents.json`. The change is reviewed like any other and my own framework is never touched.
