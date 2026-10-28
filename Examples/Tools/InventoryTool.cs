using System.Globalization;
using System.Text.Json;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Tools;

/// <summary>
/// The greenhouse ledger of The Greenhouse &amp; Nursery: catalog, stock levels and the order
/// lifecycle, read and written on the shop's shared database (see <see cref="GreenhouseDatabaseHelper"/>).
/// Unlike BillingTool and ContractTool, which only read from it, this tool is where the shop's
/// state actually moves: an order confirmed here decrements stock for everyone, in every
/// conversation, until someone cancels it.
/// </summary>
[ProvidesToolForIntent("inventory")]
public class InventoryTool : MorganaTool
{
    public InventoryTool(
        ILogger toolLogger,
        Func<ToolContext> getToolContext) : base(toolLogger, getToolContext)
    {
        // MorganaAgentAdapter constructs one InventoryTool per conversation (see
        // Activator.CreateInstance in RegisterToolsInAdapter), so this runs once per conversation,
        // not once per process — GreenhouseDatabaseHelper.Ensure's own lock+flag is what makes that safe
        // and cheap instead of re-deploying the seed file on every single conversation.
        GreenhouseDatabaseHelper.Ensure();
    }

    // =========================================================================
    // ROWS AND LOOKUPS
    // =========================================================================

    private record Product(string Sku, string Name, string Category, string Description, long QuantityOnHand, long ReorderThreshold, double UnitPrice);

    /// <summary>One plant of a cart handed to CreatePurchaseOrder, with the quantity wanted.</summary>
    public record OrderItem(string Sku, int Quantity);

    /// <summary>An order header together with every line it holds.</summary>
    private record Order(string OrderId, string Status, string? CustomerCode, string? ConversationId, string SealWord, string CreatedAt, string? ConfirmedAt, string? CancelledAt, IReadOnlyList<OrderLine> Lines);

    /// <summary>One plant of an order, numbered within it.</summary>
    private record OrderLine(long LineNumber, string Sku, long Quantity);

    /// <summary>One line of a quote: a plant of the cart with its price.</summary>
    private record QuotedLine(string Sku, string ProductName, int Quantity, double UnitPrice, double LineTotal);

    private static async Task<Product?> FindProductAsync(SqliteConnection connection, string sku)
    {
        // COLLATE NOCASE: the LLM types skus back from natural-language conversation, not from a
        // dropdown — "rse-100" and "RSE-100" must resolve to the same row rather than surfacing a
        // spurious "product not found" whenever it drops the seed data's exact casing.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku, Name, Category, Description, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products WHERE Sku = $sku COLLATE NOCASE";
        command.Parameters.AddWithValue("$sku", sku);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

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

        return productsBySku;
    }

    private static async Task<Order?> FindOrderAsync(SqliteConnection connection, string orderId)
    {
        // Same reasoning as FindProductAsync's COLLATE NOCASE: orderId travels through a chat
        // transcript, possibly retyped by a user from memory across a session boundary — comparing
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

    /// <summary>
    /// Lists the orders that one owner filter selects with their lines, reading headers and lines in a single query.
    /// </summary>
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
            string orderId = reader.GetString(0);

            // The first row of an order opens its entry.
            if (!linesByOrderId.TryGetValue(orderId, out List<object>? lines))
            {
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

        return orders;
    }

    /// <summary>
    /// Reports every line of an order whose stock no longer covers it, reading the stock of all the order's SKUs in one query.
    /// </summary>
    /// <remarks>Read after the rollback, so each figure is the stock as it stands now.</remarks>
    private static async Task<List<object>> FindShortfallsAsync(SqliteConnection connection, IReadOnlyList<OrderLine> lines)
    {
        Dictionary<string, Product> currentProducts = await FindProductsAsync(connection, lines.Select(line => line.Sku));

        // A line is short when its SKU is gone or holds fewer units than the order asks for.
        List<object> shortfalls = [];
        foreach (OrderLine line in lines)
        {
            long availableQuantity = currentProducts.TryGetValue(line.Sku, out Product? current) ? current.QuantityOnHand : 0;
            if (availableQuantity < line.Quantity)
                shortfalls.Add(new { sku = line.Sku, error = "Insufficient stock for the requested quantity", requestedQuantity = line.Quantity, availableQuantity });
        }

        return shortfalls;
    }

    /// <summary>
    /// A seal word is a short, unguessable claim-check generated ONLY by CreatePurchaseOrder and
    /// never resurfaced by any other tool afterward. This is deliberately NOT real authentication
    /// (there is no such thing as a mocked one worth having): it exists purely so that acting on
    /// or inspecting a SPECIFIC past order — possibly from a completely different session — requires
    /// something the caller could only have if they were actually given it when the order was made.
    /// </summary>
    /// <remarks>
    /// PromptHarness scenarios script the customer's side of the conversation as fixed text decided
    /// before the run, so a scenario cannot possibly recite back a value this method only invents at
    /// runtime — the two-step order flow was, until now, structurally untestable end to end. Since
    /// the word is deliberately not real security to begin with, the harness is allowed the one
    /// affordance a genuine caller never gets: <c>Harness__DeterministicSealWord</c>
    /// (<c>HarnessOptions.DeterministicSealWord</c>, republished by <c>MorganaHostFixture</c> onto
    /// the same in-process environment this plugin reads) pins this to a known constant for the
    /// run, the same env-var mechanism <see cref="Examples.Data.GreenhouseDatabaseHelper"/> already
    /// uses for StoragePath. Unset in every other environment, where this still returns a fresh
    /// random word every time.
    /// </remarks>
    private static string GenerateSealWord() =>
        Environment.GetEnvironmentVariable("Harness__DeterministicSealWord")
        is { Length: > 0 } deterministic
            ? deterministic
            : Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static async Task<List<string>> GetAllSkusAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku FROM Products ORDER BY Sku";

        List<string> skus = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            skus.Add(reader.GetString(0));

        return skus;
    }

    private static string StockStatus(long quantityOnHand, long reorderThreshold) => quantityOnHand switch
    {
        0 => "OutOfStock",
        _ when quantityOnHand <= reorderThreshold => "LowStock",
        _ => "InStock"
    };

    private static string StockStatusIcon(long quantityOnHand, long reorderThreshold) => quantityOnHand switch
    {
        0 => "🔴",
        _ when quantityOnHand <= reorderThreshold => "🟡",
        _ => "🟢"
    };

    // =========================================================================
    // TOOL METHODS
    // =========================================================================

    /// <summary>
    /// Lists every plant in the greenhouse catalog with its current stock status.
    /// </summary>
    /// <returns>JSON array of products with stock status icons.</returns>
    public async Task<string> GetProductCatalog()
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku, Name, Category, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products ORDER BY Category, Name";

        List<object> products = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                long quantity = reader.GetInt64(3);
                long threshold = reader.GetInt64(4);

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

        return JsonSerializer.Serialize(new { totalProducts = products.Count, products }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the detailed stock level for a single product.
    /// </summary>
    /// <param name="sku">Product SKU to inspect (e.g. "RSE-100").</param>
    /// <returns>JSON object with quantity, threshold, price and stock status.</returns>
    public async Task<string> CheckStockLevel(string sku)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Product? product = await FindProductAsync(connection, sku);
        if (product == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Product not found",
                requestedSku = sku,
                availableSkus = await GetAllSkusAsync(connection)
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

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
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Creates one purchase order in "Pending" status, one line per plant of the cart. This is a
    /// QUOTE, not a commitment: stock is validated but NOT decremented here. The whole order only
    /// becomes binding once ConfirmOrder is called with the returned orderId AND its single sealWord.
    /// </summary>
    /// <param name="items">Every plant of the cart with its quantity; a plant named twice is merged into one line.</param>
    /// <param name="customerCode">Identifier of the requesting customer (retrieved from shared context).</param>
    /// <returns>JSON object with the new orderId, one-time sealWord, quoted lines, total and pending status; the lines that could not be quoted instead.</returns>
    public async Task<string> CreatePurchaseOrder(List<OrderItem> items, string customerCode)
    {
        // A cart with no plant has nothing to quote.
        if (items is not { Count: > 0 })
            return JsonSerializer.Serialize(new { error = "An order needs at least one plant" }, GreenhouseDatabaseHelper.JsonOptions);

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

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
            // check and whenever (if ever) the customer comes back to actually ConfirmOrder — which
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
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // getToolContext() (not a method parameter) is the only way to reach ConversationId: it
        // is the real Akka-assigned identifier, never exposed to or writable by the LLM, so it is
        // trustworthy in a way a request/context parameter never could be — see ToolContext's
        // remarks in MorganaTool.cs. orderId + sealWord together are the claim-check pair every
        // later call to ConfirmOrder/CancelOrder must present for the whole cart; sealWord is
        // returned to the caller exactly once, right below and never stored anywhere the LLM can
        // read it back from later (no Get* tool in this class ever surfaces it again).
        ToolContext ctx = getToolContext();
        string orderId = $"ORD-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        string sealWord = GenerateSealWord();
        string createdAt = DateTime.UtcNow.ToString("O");

        // Header and lines are written together: a header missing any of its lines would be a cart
        // nobody can confirm or cancel truthfully.
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await using (SqliteCommand insertOrder = connection.CreateCommand())
            {
                insertOrder.Transaction = transaction;
                insertOrder.CommandText = """
                    INSERT INTO Orders (OrderId, Status, CustomerCode, ConversationId, SealWord, CreatedAt)
                    VALUES ($orderId, 'Pending', $customerCode, $conversationId, $sealWord, $createdAt)
                    """;
                insertOrder.Parameters.AddWithValue("$orderId", orderId);
                insertOrder.Parameters.AddWithValue("$customerCode", customerCode);
                insertOrder.Parameters.AddWithValue("$conversationId", ctx.ConversationId);
                insertOrder.Parameters.AddWithValue("$sealWord", sealWord);
                insertOrder.Parameters.AddWithValue("$createdAt", createdAt);
                await insertOrder.ExecuteNonQueryAsync();
            }

            // Line numbers follow the order of the quote, counted from one.
            foreach ((int lineIndex, QuotedLine quotedLine) in quotedLines.Index())
            {
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

        return JsonSerializer.Serialize(new
        {
            orderId,
            sealWord,
            lines = quotedLines,
            totalPrice = Math.Round(quotedLines.Sum(line => line.LineTotal), 2),
            status = "Pending",
            note = "Order created but NOT committed: stock is untouched and nothing is billed until ConfirmOrder runs on this exact orderId and sealWord. The sealWord appears in this response and in no other, now or later — ConfirmOrder and CancelOrder each require the pair, in this session and in any future one."
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Commits a Pending order: this is the only tool that actually decrements stock, for every line or none.
    /// Re-validates availability at commit time (stock may have moved since the quote).
    /// </summary>
    /// <param name="orderId">Identifier of the order to confirm. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <returns>JSON object with the confirmed lines and their remaining stock.</returns>
    public async Task<string> ConfirmOrder(string orderId, string sealWord)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Order-not-found and wrong-sealWord return the IDENTICAL message on purpose: if a wrong
        // seal word got its own distinct error, that alone would confirm to the caller that the
        // orderId exists, turning this into a guessable oracle for enumerating real orders one
        // field at a time. One combined check, one combined message, no such leak.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "No order matches that orderId and sealWord combination", requestedOrderId = orderId }, GreenhouseDatabaseHelper.JsonOptions);

        // This pre-read is ONLY for the seal-word gate and a friendly fast-path error. It is NOT the
        // state the writes below trust: between here and the commit another conversation may
        // confirm/cancel this same order or drain the shared stock. Everything that must actually be
        // true for the commit to be legal is therefore re-asserted atomically INSIDE the transaction,
        // as a WHERE clause on the write itself — the pre-read is never the authority.
        if (order.Status != "Pending")
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Order cannot be confirmed: current status is '{order.Status}', not 'Pending'",
                orderId,
                status = order.Status
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // Read once, outside the transaction, purely for the name/price that go on each invoice
        // line below — the same tolerance CreatePurchaseOrder already applies to its own quote.
        Dictionary<string, Product> productsBySku = await FindProductsAsync(connection, order.Lines.Select(line => line.Sku));

        // Claiming the order (Pending -> Confirmed) and decrementing the stock of every line must
        // all happen or none: a single transaction. Its FIRST statement is a write, so it takes
        // the write lock straight away — no SELECT-then-UPDATE lock upgrade, hence none of SQLite's
        // classic writer-upgrade deadlock — while PRAGMA busy_timeout (set by GreenhouseDatabaseHelper.OpenConnectionAsync)
        // makes a losing concurrent writer WAIT for our commit instead of throwing "database is locked".
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // One instant stamps the order and the invoice lines, so the books agree on when the sale happened.
        DateTime confirmedAtUtc = DateTime.UtcNow;
        string confirmedAt = confirmedAtUtc.ToString("O");

        // Claim the order FIRST, conditionally on it still being Pending. This WHERE clause, not the
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
            // A concurrent ConfirmOrder/CancelOrder moved this order between our pre-read and now.
            await transaction.RollbackAsync();
            Order? latest = await FindOrderAsync(connection, orderId);
            return JsonSerializer.Serialize(new
            {
                error = $"Order cannot be confirmed: current status is '{latest?.Status ?? "Unknown"}', not 'Pending'",
                orderId,
                status = latest?.Status
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // Decrement each line's stock, guarded so it can NEVER go negative: a concurrent
        // confirmation that already took the last specimens of any one plant leaves rows-affected
        // at 0 and the whole order rolls back (undoing the claim and every earlier line too) rather
        // than commit a cart that is only partly in stock. This guard, not the quote, is the truthful one.
        List<object> confirmedLines = [];
        foreach (OrderLine line in order.Lines)
        {
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
                }, GreenhouseDatabaseHelper.JsonOptions);
            }

            // Read the remaining stock back inside the SAME transaction, so the figure reported is
            // the one we just wrote — not the possibly-stale pre-read value.
            await using SqliteCommand readStock = connection.CreateCommand();
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", line.Sku);
            long remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

            confirmedLines.Add(new { sku = line.Sku, quantity = line.Quantity, remainingStock });
        }

        // Bill every line, in the same transaction: a Confirmed order with no invoice line is exactly
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

            invoiceId = await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, order.CustomerCode!, product.Name,
                product.Sku, order.OrderId, product.UnitPrice, (int)line.Quantity, confirmedAtUtc);
        }

        // Claim, stock and billing become visible together.
        await transaction.CommitAsync();

        toolLogger.LogInformation("Confirmed order {OrderId}: stock of {LineCount} line(s) decremented, billed to invoice {InvoiceId}", orderId, order.Lines.Count, invoiceId);

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
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the current status, lines and lifecycle timestamps of an existing order.
    /// </summary>
    /// <param name="orderId">Identifier of the order to inspect. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <returns>JSON object with the order's lines, status and timestamps.</returns>
    public async Task<string> GetOrderStatus(string orderId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null)
            return JsonSerializer.Serialize(new { error = "No order matches that orderId", requestedOrderId = orderId }, GreenhouseDatabaseHelper.JsonOptions);

        return JsonSerializer.Serialize(new
        {
            orderId = order.OrderId,
            lines = order.Lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }),
            status = order.Status,
            createdAt = order.CreatedAt,
            confirmedAt = order.ConfirmedAt,
            cancelledAt = order.CancelledAt
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Cancels a Pending or Confirmed order. Restores the stock of every line only if the order
    /// had already been confirmed (a Pending order never touched stock in the first place).
    /// </summary>
    /// <param name="orderId">Identifier of the order to cancel. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <param name="reason">Optional free-text cancellation reason, recorded for logging only.</param>
    /// <returns>JSON object describing the cancellation outcome.</returns>
    public async Task<string> CancelOrder(string orderId, string sealWord, string? reason = null)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Same combined not-found/wrong-sealWord check as ConfirmOrder, same reason.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "No order matches that orderId and sealWord combination", requestedOrderId = orderId }, GreenhouseDatabaseHelper.JsonOptions);

        if (order.Status == "Cancelled")
            return JsonSerializer.Serialize(new { error = "Order is already cancelled", orderId }, GreenhouseDatabaseHelper.JsonOptions);

        // Cancel and (if needed) restore stock as one atomic unit, first statement a write so we
        // take the write lock up front (no lock-upgrade deadlock; busy_timeout makes a concurrent
        // writer wait). Crucially the PREVIOUS status is decided by WHICH conditional UPDATE wins a
        // row, not by the pre-read above: that read can be stale, but only one of the two guarded
        // UPDATEs below can ever affect a row for a given order, so exactly one caller restores
        // stock for a given Confirmed->Cancelled transition — no double credit under concurrent cancels.
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        string cancelledAt = DateTime.UtcNow.ToString("O");

        // Attempt 1: claim it as a Confirmed order. Winning here (rows == 1) is the ONLY path that
        // restores stock and only one caller can ever win it.
        int confirmedRows;
        await using (SqliteCommand cancelConfirmed = connection.CreateCommand())
        {
            cancelConfirmed.Transaction = transaction;
            cancelConfirmed.CommandText = "UPDATE Orders SET Status = 'Cancelled', CancelledAt = $cancelledAt WHERE OrderId = $orderId AND Status = 'Confirmed'";
            cancelConfirmed.Parameters.AddWithValue("$cancelledAt", cancelledAt);
            cancelConfirmed.Parameters.AddWithValue("$orderId", order.OrderId);
            confirmedRows = await cancelConfirmed.ExecuteNonQueryAsync();
        }

        int pendingRows = 0;
        if (confirmedRows == 0)
        {
            // Attempt 2: it wasn't Confirmed — try to cancel it as Pending. A Pending order never
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
            // pre-read and now (Cancelled is the only other status this order could be in).
            await transaction.RollbackAsync();
            return JsonSerializer.Serialize(new { error = "Order is already cancelled", orderId }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // stockRestored is derived from which UPDATE actually won a row — atomic with the claim,
        // never from the stale pre-read.
        bool stockRestored = confirmedRows == 1;
        string previousStatus = stockRestored ? "Confirmed" : "Pending";

        if (stockRestored)
        {
            // Every line gives its units back inside the same transaction as the status change.
            foreach (OrderLine line in order.Lines)
            {
                await using SqliteCommand restoreStock = connection.CreateCommand();
                restoreStock.Transaction = transaction;
                restoreStock.CommandText = "UPDATE Products SET QuantityOnHand = QuantityOnHand + $qty WHERE Sku = $sku";
                restoreStock.Parameters.AddWithValue("$qty", line.Quantity);
                restoreStock.Parameters.AddWithValue("$sku", line.Sku);
                await restoreStock.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();

        toolLogger.LogInformation("Cancelled order {OrderId} (was {PreviousStatus}, stock restored: {StockRestored})", orderId, previousStatus, stockRestored);

        return JsonSerializer.Serialize(new
        {
            orderId,
            previousStatus,
            status = "Cancelled",
            cancelledAt,
            stockRestored,
            reason = reason ?? "Not specified"
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists the orders placed during THIS conversation, using the real Akka conversationId
    /// (never exposed to the LLM, never spoofable via a context variable) — no sealWord needed
    /// since the caller is, by construction, the same conversation that created them.
    /// </summary>
    /// <returns>JSON array of this conversation's orders with their lines (no sealWord included).</returns>
    public async Task<string> GetOrders()
    {
        // ctx.ConversationId, not a parameter: this scoping is intentionally NOT something the LLM
        // can influence or spoof — "this conversation's orders" means exactly that, decided by
        // Akka, not by whatever string a prompt-injected message might try to pass as an argument.
        ToolContext ctx = getToolContext();

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        List<object> orders = await FindOrderSummariesAsync(connection, "o.ConversationId = $owner", ctx.ConversationId);

        return JsonSerializer.Serialize(new { totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists every order ever placed by a given customer, across ALL conversations/sessions —
    /// the full history behind a customerCode, not just the current chat. Summary only: sealWord is
    /// never included here (it is shown exactly once, by CreatePurchaseOrder), so seeing this
    /// list is not enough to act on any of the orders it names.
    /// </summary>
    /// <param name="customerCode">Identifier of the customer whose order history to retrieve (retrieved from shared context).</param>
    /// <returns>JSON array of that customer's orders with their lines across every conversation (no sealWord included).</returns>
    public async Task<string> GetOrderHistory(string customerCode)
    {
        // customerCode is a shared context variable the LLM itself can write via SetContextVariable —
        // unlike GetOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access ConfirmOrder/CancelOrder do.
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        List<object> orders = await FindOrderSummariesAsync(connection, "o.CustomerCode = $owner COLLATE NOCASE", customerCode);

        return JsonSerializer.Serialize(new { customerCode, totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }
}
