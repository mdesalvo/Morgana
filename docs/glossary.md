---
title: My words
permalink: /glossary/
eyebrow: My companions
lede: I speak in the words of my craft, but every one of them has a plain technical name. Here they are side by side.
---

| My word | The technical name | What it is |
|---|---|---|
| **My grimoire** | The Morgana framework | Everything I am, plus the tomes you write |
| **A witch** of my **coven** | An agent | A specialist in one subject of your world. [Agents]({{ '/agents/' | relative_url }}) |
| **A subject** | An intent | Something your customers ask about. Each has one witch |
| **A tome** | The agent's prompt in `agents.json` | Her five chapters of words. [Prompts]({{ '/prompts/' | relative_url }}) |
| **My own tome** | `morgana.json` | My character, my rules and the words of my guards and diviner |
| **The potion** | The composed prompt | My character, my rules and her tome, brewed together before each turn |
| **My rules** | Global policies | The nine rules every witch follows |
| **A spell** | A tool | An action a witch takes on your systems |
| **An ingredient** | A tool parameter | A value a spell needs. Its scope says where it comes from |
| **A spell server** | An MCP server | Spells offered from outside over the Model Context Protocol |
| **A sister** | A consulted agent | A witch another witch may ask a question, over the A2A protocol |
| **A faraway coven** | A partner installation | Another Morgana, whose witches master other crafts and whom mine may consult if you let them |
| **The seal** | The agent class and its attributes | `[HandlesIntent]`, `[RequiresLLMTier]` and friends |
| **My familiars** | Actors | The workers that carry a message through a conversation. [Actors]({{ '/actors/' | relative_url }}) |
| **The keeper** | `ConversationManager` | The front desk of one conversation |
| **The guard** | `Guard` and the three guard rails | Who checks words coming in from outside. [Safety]({{ '/safety/' | relative_url }}) |
| **The diviner** | `Classifier` | Who decides which subject a message belongs to |
| **The guide** | `Router` | Who wakes the witch of that subject |
| **A notebook** | An agent's context | What a witch remembers in one conversation. [Context]({{ '/context/' | relative_url }}) |
| **The shared notebook** | Shared context | Values every witch of the conversation can read |
| **A mirror** | A channel | Where a customer sees me: Cauldron, Grimoire, Rune or your own. [Channels]({{ '/channels/' | relative_url }}) |
| **My charm** | Cauldron's widget | The floating button that brings me into any website |
| **The three levels** | LLM tiers | `Economy`, `Efficiency` and `Performance`, a model each |
| **Magic dust** | Dust | My coin for AI spending. 1 dust is US$ 0.015. [Models and dust]({{ '/dust/' | relative_url }}) |
| **The pouch** | The dust budget | How much dust one conversation may spend |
| **The price list** | The model catalogue, `models.json` | Each model's prices and its longest answer |
| **My still** | Alembic | Where experts' knowledge becomes tomes. [Alembic]({{ '/alembic/' | relative_url }}) |
| **My proving ground** | PromptHarness | Where I test that my witches still behave. [PromptHarness]({{ '/prompt-harness/' | relative_url }}) |
| **A plugin** | A plugin DLL | Your tomes, seals and spells, compiled, in my `plugins/` folder |
| **The territory** | `Territory` | The chapter of a tome written for her sisters, published on her A2A card |
