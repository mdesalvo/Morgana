using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI;
using Morgana.AI.Tools;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The shop assistant of The Greenhouse &amp; Nursery: catalogue, stock levels and the order lifecycle on the shop's shared database.
/// This is where the shop's state moves: an order confirmed here takes stock away for every conversation until someone cancels it.
/// </summary>
[ProvidesToolForIntent("inventory")]
public class InventoryTool : MorganaTool
{
    /// <summary>Binds the tools to the agent's context and makes sure the shop's database is deployed.</summary>
    public InventoryTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        // A tool class is built for every agent of every conversation: the helper deploys the seed once per process and skips it after.
        ExamplesDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    /// <summary>One plant of the catalogue with its stock and price.</summary>
    private record Product(string Sku, string Name, string Category, string Description, long QuantityOnHand, long ReorderThreshold, double UnitPrice);

    /// <summary>One plant of a cart handed to CreatePurchaseOrder, with the quantity wanted.</summary>
    public record OrderItem(string Sku, int Quantity);

    /// <summary>An order header together with every line it holds.</summary>
    private record Order(string OrderId, string Status, string? CustomerCode, string? ConversationId, string SealWord, string CreatedAt, string? ConfirmedAt, string? CancelledAt, IReadOnlyList<OrderLine> Lines);

    /// <summary>One plant of an order, numbered within it.</summary>
    private record OrderLine(long LineNumber, string Sku, long Quantity);

    /// <summary>One line of a quote: a plant of the cart with its price.</summary>
    private record QuotedLine(string Sku, string ProductName, int Quantity, double UnitPrice, double LineTotal);

    /// <summary>Finds one plant of the catalogue by SKU, whatever casing the model typed; null when the shop does not stock it.</summary>
    private static async Task<Product?> FindProductAsync(SqliteConnection connection, string sku)
    {
        // COLLATE NOCASE: the LLM types skus back from natural-language conversation, not from a
        // dropdown: "rse-100" and "RSE-100" must resolve to the same row rather than surfacing a
        // spurious "product not found" whenever it drops the seed data's exact casing.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku, Name, Category, Description, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products WHERE Sku = $sku COLLATE NOCASE";
        command.Parameters.AddWithValue("$sku", sku);

        // A SKU the shop does not stock finds no row.
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        // The plant as the catalogue holds it.
        return new Product(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDouble(6));
    }

    /// <summary>Looks up the plants of a cart in one query, keyed by SKU whatever casing the model typed.</summary>
    private static async Task<Dictionary<string, Product>> FindProductsAsync(SqliteConnection connection, IEnumerable<string> skus)
    {
        // The same NOCASE tolerance as FindProductAsync, applied to every SKU of the cart at once.
        List<string> skuList = [.. skus];
        Dictionary<string, Product> productsBySku = new(StringComparer.OrdinalIgnoreCase);

        // An empty cart has nothing to look up and an empty IN list is not valid SQL.
        if (skuList.Count == 0)
            return productsBySku;

        // One parameter per SKU, never spliced into the text.
        await using SqliteCommand command = connection.CreateCommand();
        string placeholders = string.Join(", ", skuList.Select((_, index) => $"$sku{index}"));
        command.CommandText = $"SELECT Sku, Name, Category, Description, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products WHERE Sku COLLATE NOCASE IN ({placeholders})";
        for (int index = 0; index < skuList.Count; index++)
            command.Parameters.AddWithValue($"$sku{index}", skuList[index]);

        // Each row found is filed under the SKU as the catalog spells it.
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Product product = new Product(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDouble(6));
            productsBySku[product.Sku] = product;
        }

        // The plants found; a SKU the shop does not stock is simply absent.
        return productsBySku;
    }

    /// <summary>Finds one order with its lines, whatever casing the identifier arrives in; null when no order carries it.</summary>
    private static async Task<Order?> FindOrderAsync(SqliteConnection connection, string orderId)
    {
        // Same reasoning as FindProductAsync's COLLATE NOCASE: orderId travels through a chat
        // transcript, possibly retyped by a user from memory across a session boundary: comparing
        // case-insensitively is what makes that forgiving instead of a needless "order not found".
        // Header and lines come in one query: one row per line, the header repeated on each.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.OrderId, o.Status, o.CustomerCode, o.ConversationId, o.SealWord, o.CreatedAt, o.ConfirmedAt, o.CancelledAt,
                   l.LineNumber, l.Sku, l.Quantity
            FROM Orders o LEFT JOIN OrderLines l ON l.OrderId = o.OrderId
            WHERE o.OrderId = $orderId COLLATE NOCASE
            ORDER BY l.LineNumber
            """;
        command.Parameters.AddWithValue("$orderId", orderId);

        // The header is taken from the first row; the list it holds receives every line as the rows come.
        Order? order = null;
        List<OrderLine> lines = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // The first row fixes the header.
            order ??= new Order(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                lines);

            // The outer join gives an order without lines a single row with no line columns.
            if (!reader.IsDBNull(8))
                lines.Add(new OrderLine(reader.GetInt64(8), reader.GetString(9), reader.GetInt64(10)));
        }

        // No row means no order under that identifier.
        return order;
    }

    /// <summary>Lists the orders that one owner filter selects with their lines, reading headers and lines in a single query.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ownerFilter">The condition on the orders that selects the owner, written over the alias <c>o</c> and the parameter <c>$owner</c>.</param>
    /// <param name="owner">The value of <c>$owner</c>.</param>
    private static async Task<List<object>> FindOrderSummariesAsync(SqliteConnection connection, string ownerFilter, string owner)
    {
        // The most recent order first with its lines in line order; the order id keeps the rows of one order together.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT o.OrderId, o.Status, o.CreatedAt, o.ConfirmedAt, o.CancelledAt, l.Sku, l.Quantity
            FROM Orders o LEFT JOIN OrderLines l ON l.OrderId = o.OrderId
            WHERE {ownerFilter}
            ORDER BY o.CreatedAt DESC, o.OrderId, l.LineNumber
            """;
        command.Parameters.AddWithValue("$owner", owner);

        // Each order is added once with a line list that the following rows of the same order fill.
        List<object> orders = [];
        Dictionary<string, List<object>> linesByOrderId = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // The order this row belongs to.
            string orderId = reader.GetString(0);

            // The first row of an order opens its entry.
            if (!linesByOrderId.TryGetValue(orderId, out List<object>? lines))
            {
                // Its line list, filed for the rows that follow, travels inside the summary itself.
                lines = [];
                linesByOrderId[orderId] = lines;
                orders.Add(new
                {
                    orderId,
                    lines,
                    status = reader.GetString(1),
                    createdAt = reader.GetString(2),
                    confirmedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                    cancelledAt = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            // The outer join gives an order without lines a single row with no line columns.
            if (!reader.IsDBNull(5))
                lines.Add(new { sku = reader.GetString(5), quantity = reader.GetInt64(6) });
        }

        // The orders most recent first, each with its lines.
        return orders;
    }

    /// <summary>
    /// Reports every line of an order whose stock no longer covers it, reading the stock of all the order's SKUs in one query.
    /// It runs after the rollback, so each figure is the stock as it stands now.
    /// </summary>
    private static async Task<List<object>> FindShortfallsAsync(SqliteConnection connection, IReadOnlyList<OrderLine> lines)
    {
        // The stock of every plant the order names, as it stands now.
        Dictionary<string, Product> currentProducts = await FindProductsAsync(connection, lines.Select(line => line.Sku));

        // A line is short when its SKU is gone or holds fewer units than the order asks for.
        List<object> shortfalls = [];
        foreach (OrderLine line in lines)
        {
            // A plant no longer stocked has none available.
            long availableQuantity = currentProducts.TryGetValue(line.Sku, out Product? currentProduct) ? currentProduct.QuantityOnHand : 0;
            if (availableQuantity < line.Quantity)
                shortfalls.Add(new { sku = line.Sku, error = "Insufficient stock for the requested quantity", requestedQuantity = line.Quantity, availableQuantity });
        }

        // Every short line of the order.
        return shortfalls;
    }

    /// <summary>
    /// Generates the seal word of a new order: a short unguessable claim-check that only CreatePurchaseOrder ever shows.
    /// It is no authentication, only proof that the caller was there when the order was made; <c>Harness__DeterministicSealWord</c>
    /// pins it for the PromptHarness, whose scripted customer cannot recite a word invented at runtime.
    /// </summary>
    private static string GenerateSealWord() =>
        // Unset everywhere but in the harness, where the word is known in advance.
        Environment.GetEnvironmentVariable("Harness__DeterministicSealWord")
        is { Length: > 0 } deterministic
            ? deterministic
            : Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>Lists every SKU of the catalogue, offered back when the model names one the shop does not stock.</summary>
    private static async Task<List<string>> GetAllSkusAsync(SqliteConnection connection)
    {
        // The catalogue in SKU order.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku FROM Products ORDER BY Sku";

        // One SKU per row.
        List<string> skus = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            skus.Add(reader.GetString(0));

        // Every SKU the shop stocks.
        return skus;
    }

    /// <summary>Names a plant's stock: out of stock at zero, low at or under the reorder threshold, in stock above it.</summary>
    private static string StockStatus(long quantityOnHand, long reorderThreshold) => quantityOnHand switch
    {
        0 => "OutOfStock",
        _ when quantityOnHand <= reorderThreshold => "LowStock",
        _ => "InStock"
    };

    /// <summary>The icon a channel shows beside a plant's stock, on the same thresholds as <see cref="StockStatus"/>.</summary>
    private static string StockStatusIcon(long quantityOnHand, long reorderThreshold) => quantityOnHand switch
    {
        0 => "🔴",
        _ when quantityOnHand <= reorderThreshold => "🟡",
        _ => "🟢"
    };

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>Lists every plant in the catalogue with its current stock status.</summary>
    [Description("Retrieves the full greenhouse/nursery catalog as structured JSON: totalProducts and an array of plants with sku, name, category, quantityOnHand, unitPrice, stockStatus (InStock/LowStock/OutOfStock) and statusIcon. This tool has only informative capabilities: it does NOT reserve, order, or modify anything.")]
    public async Task<string> GetProductCatalog()
    {
        // The catalogue is the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Every plant, grouped by category and named in order.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku, Name, Category, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products ORDER BY Category, Name";

        // One entry per plant with its stock status.
        List<object> products = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                // The stock and the reorder threshold it is judged against.
                long quantity = reader.GetInt64(3);
                long threshold = reader.GetInt64(4);

                // The plant as a customer browses it.
                products.Add(new
                {
                    sku = reader.GetString(0),
                    name = reader.GetString(1),
                    category = reader.GetString(2),
                    quantityOnHand = quantity,
                    unitPrice = reader.GetDouble(5),
                    stockStatus = StockStatus(quantity, threshold),
                    statusIcon = StockStatusIcon(quantity, threshold)
                });
            }
        }

        // The whole catalogue with its count.
        return JsonSerializer.Serialize(new { totalProducts = products.Count, products }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the detailed stock level of one plant.</summary>
    [Description("Retrieves the current stock level for a single plant as structured JSON: sku, name, category, quantityOnHand, reorderThreshold, unitPrice, stockStatus, statusIcon, maxOrderableQuantity. Returns an error with the list of valid SKUs if the sku is unknown. This tool has only informative capabilities: it does NOT reserve, order, or modify anything.")]
    public async Task<string> CheckStockLevel(
        [Description("Product SKU to inspect (e.g. 'RSE-100'). The user should specify which plant they mean, or you should present the catalog and let them choose. Do NOT guess or assume which plant the user wants.")] [ToolParameter(Records.ToolScope.Request)] string sku)
    {
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // A SKU the shop does not stock is answered with every SKU it does.
        Product? product = await FindProductAsync(connection, sku);
        if (product == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Product not found",
                requestedSku = sku,
                availableSkus = await GetAllSkusAsync(connection)
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The plant's stock, threshold and price; the stock is also the most an order can take.
        return JsonSerializer.Serialize(new
        {
            sku = product.Sku,
            name = product.Name,
            category = product.Category,
            quantityOnHand = product.QuantityOnHand,
            reorderThreshold = product.ReorderThreshold,
            unitPrice = product.UnitPrice,
            stockStatus = StockStatus(product.QuantityOnHand, product.ReorderThreshold),
            statusIcon = StockStatusIcon(product.QuantityOnHand, product.ReorderThreshold),
            maxOrderableQuantity = product.QuantityOnHand
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Creates one Pending purchase order, one line per plant of the cart: a quote, not a commitment.
    /// Stock is checked but not taken; the order binds only once ConfirmOrder receives its orderId with its seal word.
    /// </summary>
    [Description("Creates one purchase order in 'Pending' status for every plant of the cart and returns a quote as structured JSON: orderId, sealWord, lines (one per plant, each with sku, productName, quantity, unitPrice and lineTotal), totalPrice, status, note. The cart is quoted whole or not at all: when any line cannot be quoted no order is created, problems lists every such line with its reason and availableSkus lists the plants on sale when a line names one that is not. This is a QUOTE ONLY: stock is validated but NOT decremented. sealWord is shown ONLY this once, covers every line of the order and is required, together with orderId, by ConfirmOrder/CancelOrder — including in a future session. It has dispositive potential only once ConfirmOrder is called afterwards — on its own it commits nothing.")]
    public async Task<string> CreatePurchaseOrder(
        [Description("Every plant of the order with its quantity, all in this one call: each entry carries the plant's sku (e.g. 'RSE-100') and the quantity requested, which must not exceed the product's current quantityOnHand. A plant named twice is merged into one line.")] [ToolParameter(Records.ToolScope.Request)] List<OrderItem> items,
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // A cart with no plant has nothing to quote.
        if (items is not { Count: > 0 })
            return JsonSerializer.Serialize(new { error = "An order needs at least one plant" }, ExamplesDatabaseHelper.JsonOptions);

        // The catalogue is the shop's live system of record, read on every call.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // A plant named twice is one line: the model may split a quantity across entries.
        List<IGrouping<string, OrderItem>> plants = [.. items.GroupBy(item => item.Sku.Trim(), StringComparer.OrdinalIgnoreCase)];

        // One query finds every plant of the cart, so the lines below are judged without further reads.
        Dictionary<string, Product> productsBySku = await FindProductsAsync(connection, plants.Select(plant => plant.Key));

        // The cart is validated as a whole and either quoted whole or not at all: every problem of
        // every line is reported in one answer, so the customer fixes the cart once instead of
        // discovering its faults one rejected line at a time.
        List<QuotedLine> quotedLines = [];
        List<object> problems = [];
        foreach (IGrouping<string, OrderItem> plant in plants)
        {
            // The merged quantity of every entry that named this plant.
            int quantity = plant.Sum(item => item.Quantity);

            // A SKU the shop does not stock cannot be quoted. The valid ones are listed below.
            if (!productsBySku.TryGetValue(plant.Key, out Product? product))
                problems.Add(new { sku = plant.Key, error = "Product not found", requestedQuantity = (long?)null, availableQuantity = (long?)null });
            // A quantity of zero or less is not a purchase.
            else if (quantity <= 0)
                problems.Add(new { sku = product.Sku, error = "Quantity must be a positive number", requestedQuantity = (long?)quantity, availableQuantity = (long?)null });
            // This check is a courtesy at quote time, not a reservation: nothing here decrements
            // QuantityOnHand, so another conversation is free to buy the same stock between this
            // check and whenever the customer comes back to ConfirmOrder, which
            // re-runs this exact comparison itself, right before it is the one that matters.
            else if (quantity > product.QuantityOnHand)
                problems.Add(new { sku = product.Sku, error = "Insufficient stock for the requested quantity", requestedQuantity = (long?)quantity, availableQuantity = (long?)product.QuantityOnHand });
            // Everything else is a line of the quote.
            else
                quotedLines.Add(new QuotedLine(product.Sku, product.Name, quantity, product.UnitPrice, Math.Round(product.UnitPrice * quantity, 2)));
        }

        // One line that cannot be quoted keeps the whole cart from becoming an order.
        if (problems.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No order was created: the lines below cannot be quoted",
                problems,
                // The valid SKUs are listed only when a line named one the shop does not stock.
                availableSkus = plants.Any(plant => !productsBySku.ContainsKey(plant.Key)) ? await GetAllSkusAsync(connection) : null
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // The conversation comes from the tool's context, never from a parameter: the model can neither see nor write it.
        // The order id and the seal word are the pair every later ConfirmOrder and CancelOrder must present;
        // the seal word is returned once, right below: no other tool ever shows it again.
        ToolContext toolContext = getToolContext();
        string orderId = $"ORD-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        string sealWord = GenerateSealWord();
        string createdAt = DateTime.UtcNow.ToString("O");

        // Header and lines are written together: a header missing any of its lines would be a cart
        // nobody can confirm or cancel truthfully.
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            // The header starts Pending, owned by the customer and the conversation that quoted it.
            await using (SqliteCommand insertOrder = connection.CreateCommand())
            {
                insertOrder.Transaction = transaction;
                insertOrder.CommandText = """
                    INSERT INTO Orders (OrderId, Status, CustomerCode, ConversationId, SealWord, CreatedAt)
                    VALUES ($orderId, 'Pending', $customerCode, $conversationId, $sealWord, $createdAt)
                    """;
                insertOrder.Parameters.AddWithValue("$orderId", orderId);
                insertOrder.Parameters.AddWithValue("$customerCode", customerCode);
                insertOrder.Parameters.AddWithValue("$conversationId", toolContext.ConversationId);
                insertOrder.Parameters.AddWithValue("$sealWord", sealWord);
                insertOrder.Parameters.AddWithValue("$createdAt", createdAt);
                await insertOrder.ExecuteNonQueryAsync();
            }

            // Line numbers follow the order of the quote, counted from one.
            foreach ((int lineIndex, QuotedLine quotedLine) in quotedLines.Index())
            {
                // One line per plant of the quote, inside the header's transaction.
                await using SqliteCommand insertLine = connection.CreateCommand();
                insertLine.Transaction = transaction;
                insertLine.CommandText = "INSERT INTO OrderLines (OrderId, LineNumber, Sku, Quantity) VALUES ($orderId, $lineNumber, $sku, $quantity)";
                insertLine.Parameters.AddWithValue("$orderId", orderId);
                insertLine.Parameters.AddWithValue("$lineNumber", lineIndex + 1);
                insertLine.Parameters.AddWithValue("$sku", quotedLine.Sku);
                insertLine.Parameters.AddWithValue("$quantity", quotedLine.Quantity);
                await insertLine.ExecuteNonQueryAsync();
            }

            // The order exists only once the header and every line are on record.
            await transaction.CommitAsync();
        }

        toolLogger.LogInformation("Created purchase order {OrderId} with {LineCount} line(s) (user {CustomerCode})", orderId, quotedLines.Count, customerCode);

        // The quote with its seal word, shown here and nowhere else.
        return JsonSerializer.Serialize(new
        {
            orderId,
            sealWord,
            lines = quotedLines,
            totalPrice = Math.Round(quotedLines.Sum(line => line.LineTotal), 2),
            status = "Pending",
            note = "Order created but NOT committed: stock is untouched and nothing is billed until ConfirmOrder runs on this exact orderId and sealWord. The sealWord appears in this response and in no other, now or later — ConfirmOrder and CancelOrder each require the pair, in this session and in any future one."
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Commits a Pending order, the only tool that takes stock away, for every line or none, billing it as well.
    /// Availability is checked again at commit time, since stock may have moved since the quote.
    /// </summary>
    [Description("Commits a Pending order, all its lines or none: the ONLY tool that actually decrements stock AND the ONLY tool that bills the customer, one invoice line per order line. Requires the exact orderId AND sealWord pair returned by CreatePurchaseOrder — a mismatch on either is reported identically as 'no order matches', to avoid confirming or denying whether an orderId exists. Re-validates the availability of every line at commit time and returns structured JSON: orderId, lines (each with sku, quantity and remainingStock), status ('Confirmed'), confirmedAt, invoiceId, note; when the stock of any line no longer suffices nothing is committed and shortfalls lists every such line. invoiceId identifies the invoice this order was just billed to: it is the invoice's identifier and nothing else about it. This IS a dispositive, binding action — you must have asked the user for explicit confirmation before calling it. This tool has dispositive capabilities: it commits a real, persistent change to greenhouse stock and to the customer's account.")]
    public async Task<string> ConfirmOrder(
        [Description("Identifier of the Pending order to confirm, exactly as returned by CreatePurchaseOrder.")] [ToolParameter(Records.ToolScope.Request)] string orderId,
        [Description("One-time seal word returned by CreatePurchaseOrder alongside this exact orderId. If the customer does not have it, tell them plainly you need the seal word they were given when that specific order was created — never ask for a plausible-sounding guess.")] [ToolParameter(Records.ToolScope.Request)] string sealWord)
    {
        // The order and the stock are the shop's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Order-not-found and wrong-sealWord return the identical message on purpose: if a wrong
        // seal word got its own distinct error, that alone would confirm to the caller that the
        // orderId exists, turning this into a guessable oracle for enumerating real orders one
        // field at a time. One combined check, one combined message, no such leak.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "No order matches that orderId and sealWord combination", requestedOrderId = orderId }, ExamplesDatabaseHelper.JsonOptions);

        // This pre-read is only for the seal-word gate and a friendly fast-path error. It is not the
        // state the writes below trust: between here and the commit another conversation may
        // confirm/cancel this same order or drain the shared stock. Everything that must actually be
        // true for the commit to be legal is therefore re-asserted atomically inside the transaction,
        // as a WHERE clause on the write itself: the pre-read is never the authority.
        if (order.Status != "Pending")
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Order cannot be confirmed: current status is '{order.Status}', not 'Pending'",
                orderId,
                status = order.Status
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // Read once, outside the transaction, purely for the name/price that go on each invoice
        // line below, with the same tolerance CreatePurchaseOrder applies to its own quote.
        Dictionary<string, Product> productsBySku = await FindProductsAsync(connection, order.Lines.Select(line => line.Sku));

        // Claiming the order (Pending -> Confirmed) and decrementing the stock of every line must
        // all happen or none: a single transaction. Its first statement is a write, so it takes the write lock
        // straight away with no lock upgrade to deadlock on, while the busy timeout of OpenConnectionAsync
        // makes a losing concurrent writer wait for this commit instead of failing with "database is locked".
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // One instant stamps the order and the invoice lines, so the books agree on when the sale happened.
        DateTime confirmedAtUtc = DateTime.UtcNow;
        string confirmedAt = confirmedAtUtc.ToString("O");

        // The order is claimed first, conditionally on it still being Pending. This WHERE clause, not the
        // pre-read above, is what serializes two simultaneous confirmations of the same order down
        // to exactly one winner: the loser sees rows-affected 0 and bails out having touched nothing.
        int orderRows;
        await using (SqliteCommand claimOrder = connection.CreateCommand())
        {
            claimOrder.Transaction = transaction;
            claimOrder.CommandText = "UPDATE Orders SET Status = 'Confirmed', ConfirmedAt = $confirmedAt WHERE OrderId = $orderId AND Status = 'Pending'";
            claimOrder.Parameters.AddWithValue("$confirmedAt", confirmedAt);
            claimOrder.Parameters.AddWithValue("$orderId", order.OrderId);
            orderRows = await claimOrder.ExecuteNonQueryAsync();
        }

        if (orderRows == 0)
        {
            // A concurrent ConfirmOrder or CancelOrder moved this order since the pre-read: its status now is reported.
            await transaction.RollbackAsync();
            Order? latestOrder = await FindOrderAsync(connection, orderId);
            return JsonSerializer.Serialize(new
            {
                error = $"Order cannot be confirmed: current status is '{latestOrder?.Status ?? "Unknown"}', not 'Pending'",
                orderId,
                status = latestOrder?.Status
            }, ExamplesDatabaseHelper.JsonOptions);
        }

        // Each line's stock is taken, guarded so it can never go negative: a concurrent
        // confirmation that already took the last specimens of any one plant leaves rows-affected
        // at 0 and the whole order rolls back (undoing the claim and every earlier line too) rather
        // than commit a cart that is only partly in stock. This guard, not the quote, is the truthful one.
        List<object> confirmedLines = [];
        foreach (OrderLine line in order.Lines)
        {
            // The units of this line leave the stock only if the stock still holds them.
            int stockRows;
            await using (SqliteCommand updateStock = connection.CreateCommand())
            {
                updateStock.Transaction = transaction;
                updateStock.CommandText = "UPDATE Products SET QuantityOnHand = QuantityOnHand - $qty WHERE Sku = $sku AND QuantityOnHand >= $qty";
                updateStock.Parameters.AddWithValue("$qty", line.Quantity);
                updateStock.Parameters.AddWithValue("$sku", line.Sku);
                stockRows = await updateStock.ExecuteNonQueryAsync();
            }

            if (stockRows == 0)
            {
                // One line short undoes the claim and every earlier line: all the short lines are reported, not only this one.
                await transaction.RollbackAsync();
                return JsonSerializer.Serialize(new
                {
                    error = "Stock is no longer sufficient to confirm this order: nothing was confirmed",
                    orderId,
                    shortfalls = await FindShortfallsAsync(connection, order.Lines)
                }, ExamplesDatabaseHelper.JsonOptions);
            }

            // The remaining stock is read back inside the same transaction, so the figure reported is
            // the one just written and not the pre-read value.
            await using SqliteCommand readStock = connection.CreateCommand();
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", line.Sku);
            long remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

            // The line as confirmed, with what is left of its plant.
            confirmedLines.Add(new { sku = line.Sku, quantity = line.Quantity, remainingStock });
        }

        // Every line is billed in the same transaction: a Confirmed order with no invoice line is exactly
        // the half-done state this method exists to prevent for stock, so it must not exist for billing
        // either. Every line lands on the same invoice, the customer's open one for this month, which
        // the first line found or opened. A product deleted between the pre-reads and here confirms
        // its line without billing it.
        string? invoiceId = null;
        foreach (OrderLine line in order.Lines)
        {
            // A line whose product vanished is confirmed but carries no invoice line.
            if (!productsBySku.TryGetValue(line.Sku, out Product? product))
                continue;

            // Every line lands on the customer's open invoice of the month, the first one opening it when none is.
            invoiceId = await ExamplesDatabaseHelper.BillCustomerAsync(connection, transaction, order.CustomerCode!, product.Name,
                product.Sku, order.OrderId, product.UnitPrice, (int)line.Quantity, confirmedAtUtc);
        }

        // Claim, stock and billing become visible together.
        await transaction.CommitAsync();

        toolLogger.LogInformation("Confirmed order {OrderId}: stock of {LineCount} line(s) decremented, billed to invoice {InvoiceId}", orderId, order.Lines.Count, invoiceId);

        // The confirmed lines with the invoice they were billed to, which is all this response says about it.
        return JsonSerializer.Serialize(new
        {
            orderId,
            lines = confirmedLines,
            status = "Confirmed",
            confirmedAt,
            invoiceId,
            note = invoiceId == null
                ? null
                : "The order has been billed to this invoice as of this confirmation. This response carries the invoice identifier and nothing else about it: its total and its line items are not on these pages."
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>Retrieves the current status, lines and lifecycle timestamps of an existing order, never its seal word.</summary>
    [Description("Retrieves the current status and lifecycle timestamps of an existing order as structured JSON: orderId, lines (each with sku and quantity), status (Pending/Confirmed/Cancelled), createdAt, confirmedAt, cancelledAt. Keyed on the orderId alone: NO seal word is required, because reading a status commits nothing — the same fields are already listed by GetOrders and GetOrderHistory. An orderId nothing is filed under is reported as 'no order matches'. This tool has only informative capabilities: it does NOT modify the order.")]
    public async Task<string> GetOrderStatus(
        [Description("Identifier of the order to inspect, as originally returned by CreatePurchaseOrder. May belong to a past session — always trust this tool's answer over what you remember.")] [ToolParameter(Records.ToolScope.Request)] string orderId)
    {
        // The orders are the shop's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // An identifier no order carries is reported as such.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null)
            return JsonSerializer.Serialize(new { error = "No order matches that orderId", requestedOrderId = orderId }, ExamplesDatabaseHelper.JsonOptions);

        // The order's lines, status and timestamps.
        return JsonSerializer.Serialize(new
        {
            orderId = order.OrderId,
            lines = order.Lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }),
            status = order.Status,
            createdAt = order.CreatedAt,
            confirmedAt = order.ConfirmedAt,
            cancelledAt = order.CancelledAt
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Cancels a Pending or Confirmed order, giving the stock of every line back only when it had been confirmed.
    /// A Pending order never took any stock.
    /// </summary>
    [Description("Cancels a Pending or Confirmed order and returns structured JSON: orderId, previousStatus, status ('Cancelled'), cancelledAt, stockRestored, reason. Requires the exact orderId AND sealWord pair returned by CreatePurchaseOrder — a mismatch on either is reported identically as 'no order matches'. If the order had been Confirmed, the stock of every line is restored; a Pending order never touched stock so none is restored. This tool has dispositive capabilities: it commits a real, persistent change to an order and possibly to greenhouse stock.")]
    public async Task<string> CancelOrder(
        [Description("Identifier of the order to cancel, exactly as returned by CreatePurchaseOrder or previously discussed.")] [ToolParameter(Records.ToolScope.Request)] string orderId,
        [Description("One-time seal word returned by CreatePurchaseOrder for this exact orderId.")] [ToolParameter(Records.ToolScope.Request)] string sealWord,
        [Description("Optional reason for the cancellation (e.g., 'changed my mind', 'found a better price'). Recorded for internal purposes but not required. Ask the user if they'd like to provide one, but make it clear it's optional.")] [ToolParameter(Records.ToolScope.Request)] string? reason = null)
    {
        // The order and the stock are the shop's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Same combined not-found/wrong-sealWord check as ConfirmOrder, same reason.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "No order matches that orderId and sealWord combination", requestedOrderId = orderId }, ExamplesDatabaseHelper.JsonOptions);

        // An order already cancelled has nothing left to cancel.
        if (order.Status == "Cancelled")
            return JsonSerializer.Serialize(new { error = "Order is already cancelled", orderId }, ExamplesDatabaseHelper.JsonOptions);

        // Cancelling and giving stock back are one transaction whose first statement is a write, so it takes the lock up front.
        // The previous status is decided by which conditional UPDATE wins a row, never by the pre-read: only one of the two
        // can affect a row, so exactly one caller gives stock back for a confirmed order cancelled twice at once.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // One instant stamps the cancellation.
        string cancelledAt = DateTime.UtcNow.ToString("O");

        // First the order is claimed as Confirmed: winning a row here is the only path that gives stock back
        // and only one caller can ever win it.
        int confirmedRows;
        await using (SqliteCommand cancelConfirmed = connection.CreateCommand())
        {
            cancelConfirmed.Transaction = transaction;
            cancelConfirmed.CommandText = "UPDATE Orders SET Status = 'Cancelled', CancelledAt = $cancelledAt WHERE OrderId = $orderId AND Status = 'Confirmed'";
            cancelConfirmed.Parameters.AddWithValue("$cancelledAt", cancelledAt);
            cancelConfirmed.Parameters.AddWithValue("$orderId", order.OrderId);
            confirmedRows = await cancelConfirmed.ExecuteNonQueryAsync();
        }

        // Then, if it was not Confirmed, it is cancelled as Pending.
        int pendingRows = 0;
        if (confirmedRows == 0)
        {
            // A Pending order is cancelled with nothing to give back. A Pending order never
            // reached ConfirmOrder, so it never decremented stock: this path restores nothing.
            await using SqliteCommand cancelPending = connection.CreateCommand();
            cancelPending.Transaction = transaction;
            cancelPending.CommandText = "UPDATE Orders SET Status = 'Cancelled', CancelledAt = $cancelledAt WHERE OrderId = $orderId AND Status = 'Pending'";
            cancelPending.Parameters.AddWithValue("$cancelledAt", cancelledAt);
            cancelPending.Parameters.AddWithValue("$orderId", order.OrderId);
            pendingRows = await cancelPending.ExecuteNonQueryAsync();
        }

        if (confirmedRows == 0 && pendingRows == 0)
        {
            // Neither claim won a row: a concurrent CancelOrder already cancelled it between our
            // pre-read and now, Cancelled being the only other status this order could be in.
            await transaction.RollbackAsync();
            return JsonSerializer.Serialize(new { error = "Order is already cancelled", orderId }, ExamplesDatabaseHelper.JsonOptions);
        }

        // Whether stock goes back follows from which UPDATE won a row, atomic with the claim and never from the pre-read.
        bool stockRestored = confirmedRows == 1;
        string previousStatus = stockRestored ? "Confirmed" : "Pending";

        // Only a confirmed order took stock, so only it gives stock back.
        if (stockRestored)
        {
            // Every line gives its units back inside the same transaction as the status change.
            foreach (OrderLine line in order.Lines)
            {
                // The line's units return to its plant.
                await using SqliteCommand restoreStock = connection.CreateCommand();
                restoreStock.Transaction = transaction;
                restoreStock.CommandText = "UPDATE Products SET QuantityOnHand = QuantityOnHand + $qty WHERE Sku = $sku";
                restoreStock.Parameters.AddWithValue("$qty", line.Quantity);
                restoreStock.Parameters.AddWithValue("$sku", line.Sku);
                await restoreStock.ExecuteNonQueryAsync();
            }
        }

        // The cancellation and the returned stock become visible together.
        await transaction.CommitAsync();

        toolLogger.LogInformation("Cancelled order {OrderId} (was {PreviousStatus}, stock restored: {StockRestored})", orderId, previousStatus, stockRestored);

        // The cancellation with what it was and whether stock went back.
        return JsonSerializer.Serialize(new
        {
            orderId,
            previousStatus,
            status = "Cancelled",
            cancelledAt,
            stockRestored,
            reason = reason ?? "Not specified"
        }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists the orders placed during this conversation, never their seal words.
    /// The conversation comes from the tool's context, which the model can neither see nor write, so no seal word is needed.
    /// </summary>
    [Description("Lists the orders placed during THIS conversation as structured JSON: totalOrders and an array of order summaries (orderId, lines (each with sku and quantity), status, createdAt, confirmedAt, cancelledAt). Does NOT include sealWord (shown only once, by CreatePurchaseOrder) so this listing alone is never enough to confirm or cancel anything. This tool has only informative capabilities.")]
    public async Task<string> GetOrders()
    {
        // "This conversation's orders" means exactly that, decided by Morgana and never by a string an injected message could pass.
        ToolContext toolContext = getToolContext();

        // The orders are the shop's live system of record.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Every order the conversation placed, with its lines.
        List<object> orders = await FindOrderSummariesAsync(connection, "o.ConversationId = $owner", toolContext.ConversationId);

        // The orders with their count.
        return JsonSerializer.Serialize(new { totalOrders = orders.Count, orders }, ExamplesDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists every order a customer ever placed, across every conversation, as a summary without seal words.
    /// Seeing this list is not enough to act on any order it names.
    /// </summary>
    [Description("Lists EVERY order a customer has ever placed, across ALL conversations/sessions — not just this one — as structured JSON: customerCode, totalOrders and an array of order summaries (orderId, lines (each with sku and quantity), status, createdAt, confirmedAt, cancelledAt). Does NOT include sealWord (shown only once, by CreatePurchaseOrder) so this listing alone is never enough to confirm or cancel any order it names — that pair is what ConfirmOrder and CancelOrder require. To re-read one single order by its identifier, use GetOrderStatus. This tool has only informative capabilities.")]
    public async Task<string> GetOrderHistory(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code. It scopes the listing to that one customer and is not a selector for choosing whose history to read.")] [ToolParameter(Records.ToolScope.SharedContext)] string customerCode)
    {
        // customerCode is a shared context variable the model itself can write through SetContextVariable:
        // unlike GetOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access ConfirmOrder/CancelOrder do.
        await using SqliteConnection connection = await ExamplesDatabaseHelper.OpenConnectionAsync();

        // Every order under the code, matched ignoring case.
        List<object> orders = await FindOrderSummariesAsync(connection, "o.CustomerCode = $owner COLLATE NOCASE", customerCode);

        // The orders with their count.
        return JsonSerializer.Serialize(new { customerCode, totalOrders = orders.Count, orders }, ExamplesDatabaseHelper.JsonOptions);
    }
}