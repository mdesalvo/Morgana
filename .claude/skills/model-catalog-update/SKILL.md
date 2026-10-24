---
name: model-catalog-update
description: Brings the model catalogue of Morgana's provider plugins up to date with the vendors' official lineups and prices. Use when the user says things like "update the model catalogue", "aggiorna il catalogo dei modelli", "refresh the model prices", "are our model prices current?".
---

# model-catalog-update

Every provider plugin prices the models it serves in `GetModelPricing`, and
`MorganaLanguageModel.ConvertToMorganaDust` turns those dollars into dust. A tier whose model is
missing from its provider's table does not start. Keeping the tables current is periodic
maintenance: vendors add models, change prices, end promotions and retire models.

## Scope

| Plugin | File | Official sources |
|---|---|---|
| Anthropic | `Morgana/Morgana.AI.LanguageModels/Anthropic/AnthropicLanguageModel.cs` | https://platform.claude.com/docs/en/about-claude/pricing · https://platform.claude.com/docs/en/about-claude/models/overview · https://platform.claude.com/docs/en/about-claude/model-deprecations |
| OpenAI | `Morgana/Morgana.AI.LanguageModels/OpenAI/OpenAILanguageModel.cs` | https://developers.openai.com/api/docs/pricing · https://developers.openai.com/api/docs/deprecations |
| Google | `Morgana/Morgana.AI.LanguageModels/Google/GoogleLanguageModel.cs` | https://ai.google.dev/gemini-api/docs/pricing · https://ai.google.dev/gemini-api/docs/models · https://ai.google.dev/gemini-api/docs/deprecations |

Ollama is out of scope: it prices every model at zero. Where a source URL has moved, follow the
vendor's redirect. Prices and model ids come only from these official pages, never from memory,
third-party aggregators or gateways.

## What is catalogued

A model enters a table only when all of these hold:

- it is **generally available to the public**: never a preview, experimental or early-access
  model (an id or a label carrying `preview` or `exp`), never one behind a limited-access program
  (Claude Mythos, for instance), never one whose vendor reserves it to verified organizations;
- it is a **text chat model** that can serve a tier: no audio, live, TTS, transcription, image,
  video, music, embedding, moderation, robotics or search-specialized variants;
- it **supports function calling**, as the vendor's own model page states: every agent turn closes
  with a tool call;
- it is **not retired** on the vendor's own API;
- the vendor publishes its **input, output and cache-read prices**. A model listed without a
  cache-read price (the OpenAI `-pro` models, for instance) is not catalogued.

A model enters under its official name alone (`claude-haiku-4-5`, `gpt-4o`): never a dated
snapshot id or any other alias of it.

## How a price is read

Each entry is `new Records.ModelPricing(input, output, cacheRead, cacheWrite)`, in US dollars per
million tokens, as `decimal` literals:

- **standard processing**: never batch, flex, priority, fast or regional uplifts;
- **the shortest prompt band** where the vendor prices by prompt length (Claude Haiku 5.5 up to
  100,000 tokens, Gemini up to 200,000, OpenAI up to 272,000 at the time of writing);
- **the price in force once a promotion ends**, when the vendor states it. A promotion with no
  stated later price keeps its current price: the report says so;
- **text input** where the vendor prices modalities apart;
- **cache write** at the lifetime that the plugin's client asks for:
  - Anthropic: the 1-hour write, since `MorganaAnthropicClient` caches for an hour;
  - OpenAI: the 30-minute write, OpenAI's default, where the vendor bills one; otherwise the input price;
  - Google: the input price, since implicit caching bills no write.

If a client changes the cache lifetime that it asks for, the cache-write column of that whole table
changes with it.

## Procedure

1. Read the current table of each plugin in scope.
2. Fetch every official source above and build, per vendor, the list of models that qualify.
3. Compare the two and sort every difference into one of four groups:
   - **new**: a qualifying model missing from the table;
   - **repriced**: a catalogued model whose price changed, including a promotion that ended;
   - **leaving**: a catalogued model that the vendor has retired, has scheduled for retirement or
     no longer lists;
   - **not catalogued**: a model seen on the sources but excluded by the rules above, with the rule.
4. Apply **new** and **repriced** to the tables. Edits follow the `code-commentation` skill and the
   comment above each table stays true (vendor, band, cache-write lifetime). Object creation is
   always `new Records.ModelPricing(...)`, never target-typed `new(...)`.
5. **Never remove a leaving model on your own.** Report each one with its retirement date and the
   vendor's suggested replacement, then agree with the user on what to do: remove it now, keep it
   until its date or keep it.
6. Check the tiers that the deployment uses: read the `ModelId` of each tier in the User Secrets
   of `Morgana.Web` (never print a key) and report any tier whose model is leaving or no longer
   priced, since that tier will refuse to start.
7. Build `Morgana/Morgana.slnx` and run
   `dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.LanguageModelTests"`
   from the repository root. It calls no model.
8. Report, as one table per vendor, the four groups with old and new prices and the source of
   each. State every promotion read and every judgement call. Do not commit: the user decides.
