---
name: model-catalog-update
description: Brings the model catalogue of Morgana's provider plugins up to date with the vendors' official lineups and prices. Use when the user says things like "update the model catalogue", "aggiorna il catalogo dei modelli", "refresh the model prices", "are our model prices current?".
---

# model-catalog-update

Every provider plugin embeds a mandatory `pricing.json` listing the models it serves with their
prices. `MorganaLanguageModel` reads it and turns those dollars into dust. A tier whose model is
missing from its provider's `pricing.json` does not start. Keeping the files current is periodic
maintenance: vendors add models, change prices, end promotions and retire models.

## Scope

| Plugin | File | Official sources |
|---|---|---|
| Anthropic | `Morgana/Morgana.AI.LanguageModels/Anthropic/pricing.json` | https://platform.claude.com/docs/en/about-claude/pricing · https://platform.claude.com/docs/en/about-claude/models/overview · https://platform.claude.com/docs/en/about-claude/model-deprecations |
| Google | `Morgana/Morgana.AI.LanguageModels/Google/pricing.json` | https://ai.google.dev/gemini-api/docs/pricing · https://ai.google.dev/gemini-api/docs/models · https://ai.google.dev/gemini-api/docs/deprecations |
| OpenAI | `Morgana/Morgana.AI.LanguageModels/OpenAI/pricing.json` | https://developers.openai.com/api/docs/pricing · https://developers.openai.com/api/docs/deprecations |
| Ollama | `Morgana/Morgana.AI.LanguageModels/Ollama/pricing.json` | https://ollama.com/pricing · https://ollama.com/api/tags (the cloud lineup) · `POST https://ollama.com/api/show` with `{"model": "<name>"}` (the capabilities of one model) |

Ollama's catalogue holds only the **cloud models** that ollama.com hosts and bills per token. A local
model costs nothing to Ollama's account, so it is never catalogued and a tier running one declares its own
`Pricing`, zero or the deployment's hosting cost. The same holds for any provider that hosts models
under names its customers choose (Azure OpenAI deployments, for instance). A deployment that needs a model before
the next release catalogues it, or pays a negotiated price, declares `Pricing` on its tier too: the
catalogue is the default that such a tier overlays. Where a source URL has moved, follow the vendor's redirect. Prices and
model ids come only from these official pages, never from memory, third-party aggregators or
gateways.

## What is catalogued

A model enters a file only when all of these hold:

- it is **generally available to the public**: never a preview, experimental or early-access
  model (an id or a label carrying `preview` or `exp`), never one behind a limited-access program
  (Claude Mythos, for instance), never one whose vendor reserves it to verified organizations;
- it is a **text chat model** that can serve a tier: no audio, live, TTS, transcription, image,
  video, music, embedding, moderation, robotics or search-specialized variants;
- it **supports function calling**, as the vendor's own model page states: every agent turn closes
  with a tool call. For Ollama the statement is `/api/show`: its `capabilities` hold both
  `completion` and `tools`;
- it is **not retired** on the vendor's own API. Ollama publishes no deprecation page: a model that
  `/api/tags` no longer lists or `/api/show` no longer finds counts as retired;
- the vendor publishes its **input, output and cache-read prices**. A model listed without a
  cache-read price is not catalogued: the OpenAI `-pro` models, for instance. Ollama is the
  exception for the cache-read column alone: a `-` there means the model reads no prompt at a
  discount, so its cache read is its input price. A `-` in Ollama's input or output column keeps
  the model out.

A model enters under its official name alone (`claude-haiku-4-5`, `gpt-4o`): never a dated
snapshot id or any other alias of it. For Ollama the official name is the one on the pricing page
(`gemma4`, `gpt-oss:120b`), whatever parameter size ollama.com serves behind it: Ollama bills a
family at one price. The name must also be one that ollama.com answers to, which `/api/show` proves:
where the pricing page name is refused there, the model enters under the one name that `/api/tags`
lists for that family (`mistral-large-3:675b`). Never the `:cloud` or `-cloud` form that a local daemon uses (`gemma4:cloud`, `gpt-oss:120b-cloud`):
the plugin prices that form under the catalogue name by itself. Never a version tag either
(`deepseek-v4-pro:0813`).

## How a price is read

Each entry of `Models` is one line, in US dollars per million tokens:

```json
{ "Name": "claude-sonnet-5-5", "Pricing": { "InputUsdPerMillion": 2.00, "OutputUsdPerMillion": 10.00, "CacheReadUsdPerMillion": 0.10, "CacheWriteUsdPerMillion": 4.00 } }
```

`Name` is matched against a tier's `ModelId` with its exact casing and appears once per file. The
prices follow these rules:

- **standard processing**: never batch, flex, priority, fast or regional uplifts;
- **the peak price** where the vendor discounts some hours: Ollama's `(Off-Peak)` rows are never
  read, since a tier is billed around the clock and dust has one price;
- **the shortest prompt band** where the vendor prices by prompt length (Claude Haiku 5.5 up to
  100,000 tokens, Gemini up to 200,000, OpenAI up to 272,000 at the time of writing);
- **the price in force once a promotion ends**, when the vendor states it. A promotion with no
  stated later price keeps its current price: the report says so;
- **text input** where the vendor prices modalities apart;
- **cache write** at the lifetime that the plugin's client asks for:
  - Anthropic: the 1-hour write, since `MorganaAnthropicClient` caches for an hour;
  - OpenAI: the 30-minute write, OpenAI's default, where the vendor bills one; otherwise the input price;
  - Google: the input price, since implicit caching bills no write;
  - Ollama: the input price, since ollama.com bills no write.

JSON carries no comments, so these choices are stated in the `<remarks>` of each plugin's
`{Provider}LanguageModel` class. If a client changes the cache lifetime that it asks for, the
cache-write column of that whole file changes with it and so do those remarks.

## Procedure

1. Read the current `pricing.json` of each plugin in scope.
2. Fetch every official source above and build, per vendor, the list of models that qualify. For
   Ollama, call `/api/show` once for every model that the pricing page lists: it needs no key.
3. Compare the two and sort every difference into one of four groups:
   - **new**: a qualifying model missing from the file;
   - **repriced**: a catalogued model whose price changed, including a promotion that ended;
   - **leaving**: a catalogued model that the vendor has retired, has scheduled for retirement or
     no longer lists;
   - **not catalogued**: a model seen on the sources but excluded by the rules above, with the rule.
4. Apply **new** and **repriced** to the files, keeping the vendor's family order. The `<remarks>`
   of each plugin class must stay true (vendor, band, cache-write lifetime): where one changes, the
   edit follows the `code-commentation` skill.
5. **Never remove a leaving model on your own.** Report each one with its retirement date and the
   vendor's suggested replacement, then agree with the user on what to do: remove it now, keep it
   until its date or keep it.
6. Check the tiers that the deployment uses: read the `ModelId` of each tier in the User Secrets
   of `Morgana.Web` (never print a key) and report any tier whose model is leaving or no longer
   priced, since that tier will refuse to start.
7. Build `Morgana/Morgana.slnx` and run
   `dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.LanguageModelTests"`
   from the repository root. It calls no model. `LanguageModel_The_dust_of_a_tier_is_its_models_price_converted`
   pins the dust of Claude Sonnet 5.5: a repricing of that model changes the test too.
8. Report, as one table per vendor, the four groups with old and new prices and the source of
   each. State every promotion read and every judgement call. Do not commit: the user decides.
