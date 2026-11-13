# Examples — the example plugin

Four agents of the fictional shop *The Greenhouse & Nursery* and the MCP server one of them draws
its tools from. It is what an author reads to learn how a domain is written for Morgana.

## What it shows

- **One organization, several agents**: three roles of one shop on **one** SQLite system of record
  (`Data/Examples.sqlite`, seeded and rebased onto the current month at deployment by
  `ExamplesDatabaseHelper.Ensure`). `InventoryAgent` confirming an order and `ContractAgent` enrolling
  a plan are the two dispositive actions; both bill through `ExamplesDatabaseHelper.BillCustomerAsync`,
  so the books agree whichever agent closes a sale. `BillingAgent` only reads.
- **Agents consulting each other**: `Billing → Inventory` and `Contract → Billing`, a chain and not a
  triangle, so the refusal of a second hop is exercised too.
- **Tools acquired at runtime**: `GardeningAgent` has no tool class and an empty context vocabulary;
  its tools come from `GardeningMCP`, which reads the almanac from four tables grafted onto `Products`
  by `Sku`. A plant without a care sheet has no page. A page is found from the customer's own words for
  the plant, through the names it goes by; the sku is only a second door.

## Before touching it

- The `[Description]` of a tool and of its parameters, `agents.json` and what a tool returns (`note`,
  `message`, `error`) are prose the model reads, not comments: they change only with a PromptHarness
  run that measures the change.
- The seed database is embedded in the assembly; a change to its shape changes the scenarios built on it.
- `GardeningMCP` answers at `GardeningAgent.AlmanacServerAddress` (http://localhost:5006/mcp), the
  address the agent names: the PromptHarness starts it there on its own, so nothing else may hold the port.