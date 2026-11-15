<a href="#"><img src="https://github.com/mdesalvo/Morgana/blob/main/Channels/Cauldron/Assets/Morgana-Banner.jpg" alt="Morgana Logo" width="100%" /></a>

<p>
  <img src="https://img.shields.io/badge/.NET-10-932BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/Akka.NET-932BD4?logo=nuget" alt="Akka.NET"/>
  <img src="https://img.shields.io/badge/Microsoft.Agents.AI-932BD4?logo=nuget" alt="Microsoft.Agents.AI"/>
  <a href="https://hub.docker.com/r/mdesalvo/morgana"><img src="https://img.shields.io/docker/pulls/mdesalvo/morgana?logo=docker&logoColor=white&label=Morgana&color=9f7aea" alt="Morgana (Docker Pulls)"></a>
  <a href="https://hub.docker.com/r/mdesalvo/cauldron"><img src="https://img.shields.io/docker/pulls/mdesalvo/cauldron?logo=docker&logoColor=white&label=Cauldron&color=9f7aea" alt="Cauldron (Docker Pulls)"></a>
  <a href="https://hub.docker.com/r/mdesalvo/grimoire"><img src="https://img.shields.io/docker/pulls/mdesalvo/grimoire?logo=docker&logoColor=white&label=Grimoire&color=9f7aea" alt="Grimoire (Docker Pulls)"></a>
  <a href="https://hub.docker.com/r/mdesalvo/rune"><img src="https://img.shields.io/docker/pulls/mdesalvo/rune?logo=docker&logoColor=white&label=Rune&color=9f7aea" alt="Rune (Docker Pulls)"></a>
  <a href="https://hub.docker.com/r/mdesalvo/alembic"><img src="https://img.shields.io/docker/pulls/mdesalvo/alembic?logo=docker&logoColor=white&label=Alembic&color=9f7aea" alt="Alembic (Docker Pulls)"></a>
</p>

Morgana is a modern and flexible **conversational AI framework** designed to handle complex scenarios through a sophisticated **multi-agent, semantic-routing architecture**. Built on **.NET 10** and leveraging the actor model via **Akka.NET**, Morgana orchestrates specialized **AI agents** that collaborate to understand, classify and resolve customer inquiries with precision and context awareness.

The system is powered by **Microsoft.Agents.AI**, enabling seamless integration with Large Language Models (LLMs) while maintaining strict governance through guard rails and policy enforcement.

<p align="center">
  <a href="https://mdesalvo.github.io/Morgana/"><b>📖 Read the manual</b></a>: Morgana tells her own story, from her six pillars to the few choices that are yours to make
</p>

---

## Core Philosophy

Morgana **reimagines conversational AI** through 6 foundational pillars that **work in harmony** to deliver an **orchestration framework** that is powerful yet **remarkably simple to configure**.

| | Pillar | In one line |
|---|---|---|
| 🎭 | [**Actors**](https://mdesalvo.github.io/Morgana/actors/) | Resilient multi-channel orchestration through Akka.NET message-driven architecture |
| 🤖 | [**Agents**](https://mdesalvo.github.io/Morgana/agents/) | Declarative specialization with automatic discovery and dynamic capabilities (MCP + A2A) |
| 📝 | [**Prompts**](https://mdesalvo.github.io/Morgana/prompts/) | First-class artifacts with layered personality architecture and structured behavioral policies |
| 💾 | [**Context**](https://mdesalvo.github.io/Morgana/context/) | Private by default, self-synchronizing where it matters |
| 📡 | [**Channels**](https://mdesalvo.github.io/Morgana/channels/) | One brain, every surface: rich where it can be, plain where it must be |
| ✨ | [**Authoring**](https://mdesalvo.github.io/Morgana/authoring/) | An AI-driven workbench that distils domain expertise into production-ready agents |

---

## 🚀 Quick Start

<details open>
<summary><b>⚙️ Setup</b>: <i>once, before any channel</i></summary>

```bash
# 📋 Copy the development template
cp development.env.template .env

# ✏️ Configure your secrets
nano .env

# 🔨 Build .NET projects (from project root)
dotnet build ./Morgana
dotnet build ./Channels/Cauldron
dotnet build ./Channels/Grimoire
dotnet build ./Channels/Rune

# 🐳 Build Docker images
docker compose --env-file .env --env-file .env.versions build
```

</details>

<details>
<summary><b>🌐 Morgana on Cauldron</b>: <i>the browser channel and the stack everything else talks to</i></summary>

```bash
# 🚀 Start the containers (Morgana + Cauldron + the gardening MCP server)
docker compose --env-file .env --env-file .env.versions up

# ✅ Open your browser at http://localhost:5002

# 🛑 Stop the containers (when you are done, whichever channel you used)
docker compose --env-file .env --env-file .env.versions down
```

</details>

<details>
<summary><b>📟 Morgana on Grimoire</b>: <i>the rich TTY channel, on the stack started above</i></summary>

```bash
docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases grimoire
```

</details>

<details>
<summary><b>📜 Morgana on Rune</b>: <i>the basic TTY channel, same stack as Grimoire</i></summary>

```bash
docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases rune
```

</details>

<details>
<summary><b>⚗️ Alembic</b>: <i>the authoring workbench, which needs no Morgana instance</i></summary>

```bash
# 🔨 Build it
dotnet build ./Alembic

# 🧪 Model a domain, at http://localhost:5005
docker compose --env-file .env --env-file .env.versions --profile authoring up alembic
```

</details>
