---
title: "💾 Context"
permalink: /context/
eyebrow: Pillar 4 of 6
tagline: Private by default, self-synchronizing where it matters
lede: What I remember, who in my coven may see it and how I keep a long conversation from growing too heavy to carry.
---

## Each witch keeps her own notebook

Every witch of my coven keeps a private notebook of the conversation: what was said to her and the values she wrote down. No sister can read or change it. That is what lets several specialists work side by side without confusing each other.

## Some notes travel

A few facts should not stay in one notebook. If a customer gave their customer code to my accounts witch, my contracts witch must not ask for it again a minute later.

So a spell can mark a value as **shared**. As soon as one witch learns a shared value, every witch of that conversation knows it from her next turn. In my shop the customer code is shared:

<ol class="steps" markdown="1">
<li markdown="1">

The customer asks my accounts witch for their invoices. She asks for the customer code and the customer writes *P994E*.

</li>
<li markdown="1">

She writes `customerCode = P994E` into the shared notebook.

</li>
<li markdown="1">

The customer asks about their care plan. My contracts witch takes over, already knows the code and answers straight away.

</li>
</ol>

The first value written wins. A later witch cannot overwrite it with a guess, so the customer always sees one consistent conversation.

Making a value shared takes one word on the spell's ingredient, `SharedContext`, as shown in [Agents]({{ '/agents/#spells-how-a-witch-acts' | relative_url }}).

## I forget nothing when I sleep

I keep every conversation in its own small file, **sealed with your encryption key**. When I restart, when the customer refreshes the page or comes back the next day, I rebuild the conversation as it was: the messages, the buttons, the cards and the witch who was speaking.

## Long conversations get a summary

AI models charge for everything they read and every turn re-reads the conversation so far. A conversation that runs long would cost more and more dust.

So once a conversation passes a number of messages, I write a **summary** of the older part and keep only the summary plus the latest messages. My summaries are careful with two things a careless one would lose: what the witch really did with her spells and the exact codes, numbers and amounts, which I copy word for word.

In my terminal mirrors a customer can also ask for a summary at any moment with the `/compact` command.

## What you may adjust

| Setting | What it is for | My default |
|---|---|---|
| `ConversationPersistence:StoragePath` | The folder where I keep the conversation files | none, you choose it |
| `ConversationPersistence:EncryptionKey` | The key I seal them with | none, you forge it |
| `HistoryReducer:Enabled` | Switches my summaries on or off | on |
| `HistoryReducer:SummarizationThreshold` | How many messages a conversation may reach before I summarise it | 12 |
| `HistoryReducer:SummarizationTargetCount` | How many recent messages I keep beside the summary | 8 |
| `HistoryReducer:SummarizationPrompt` | The words I write my summaries from | a careful default |

> Keep the encryption key safe and keep a copy apart from the files. Without it nobody can read the stored conversations, not even me.
{: .warn}
