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
    private record Order(string OrderId, string Status, string? CustomerCode, string? ConversationId, string SealWord, string CreatedAt, string? ConfirmedAt, string? CancelledAt, IReadOnlyList<OrderLine> Lines);
    private record OrderLine(long LineNumber, string Sku, long Quantity);
    public record OrderItem(string Sku, int Quantity);

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

    private static async Task<Order?> FindOrderAsync(SqliteConnection connection, string orderId)
    {
        // Same reasoning as FindProductAsync's COLLATE NOCASE: orderId travels through a chat
        // transcript, possibly retyped by a user from memory across a session boundary — comparing
        // case-insensitively is what makes that forgiving instead of a needless "order not found".
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, Status, CustomerCode, ConversationId, SealWord, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE OrderId = $orderId COLLATE NOCASE";
        command.Parameters.AddWithValue("$orderId", orderId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        string storedOrderId = reader.GetString(0);
        return new Order(
            storedOrderId,
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            await FindOrderLinesAsync(connection, storedOrderId));
    }

    private static async Task<List<OrderLine>> FindOrderLinesAsync(SqliteConnection connection, string orderId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT LineNumber, Sku, Quantity FROM OrderLines WHERE OrderId = $orderId ORDER BY LineNumber";
        command.Parameters.AddWithValue("$orderId", orderId);

        List<OrderLine> lines = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(new OrderLine(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));

        return lines;
    }

    /// <summary>
    /// Reads the order summaries matching <paramref name="ownerFilter"/>, newest first, each with
    /// its lines and never with its seal word: the one listing shape GetOrders and GetOrderHistory share.
    /// </summary>
    private static async Task<List<object>> FindOrderSummariesAsync(SqliteConnection connection, string ownerFilter, string parameterName, string parameterValue)
    {
        List<(string OrderId, string Status, string CreatedAt, string? ConfirmedAt, string? CancelledAt)> headers = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT OrderId, Status, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE {ownerFilter} ORDER BY CreatedAt DESC";
            command.Parameters.AddWithValue(parameterName, parameterValue);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                headers.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        List<object> orders = [];
        foreach ((string orderId, string status, string createdAt, string? confirmedAt, string? cancelledAt) in headers)
        {
            List<OrderLine> lines = await FindOrderLinesAsync(connection, orderId);
            orders.Add(new
            {
                orderId,
                lines = lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }),
                status,
                createdAt,
                confirmedAt,
                cancelledAt
            });
        }

        return orders;
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

    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string customerCode)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Customers WHERE CustomerCode = $customerCode COLLATE NOCASE";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        return (string?)await command.ExecuteScalarAsync();
    }

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
    /// Creates a new purchase order in "Pending" status, one line per plant of the cart. This is a
    /// QUOTE, not a commitment: stock is validated but NOT decremented here. The whole order only
    /// becomes binding once ConfirmOrder is called with the returned orderId AND its single sealWord.
    /// </summary>
    /// <param name="items">Every plant of the cart with its quantity; a plant named twice is merged into one line.</param>
    /// <param name="customerCode">Identifier of the requesting customer (retrieved from shared context).</param>
    /// <returns>JSON object with the new orderId, one-time sealWord, quoted lines, total and pending status.</returns>
    public async Task<string> CreatePurchaseOrder(List<OrderItem> items, string customerCode)
    {
        if (items is not { Count: > 0 })
            return JsonSerializer.Serialize(new { error = "An order needs at least one plant" }, GreenhouseDatabaseHelper.JsonOptions);

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // The cart is validated as a whole and either quoted whole or not at all: every problem of
        // every line is reported in one answer, so the customer fixes the cart once instead of
        // discovering its faults one rejected line at a time.
        List<(Product Product, int Quantity)> quotedLines = [];
        List<object> problems = [];
        bool anyUnknownSku = false;
        foreach (IGrouping<string, OrderItem> plant in items.GroupBy(item => item.Sku.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            int quantity = plant.Sum(item => item.Quantity);
            Product? product = await FindProductAsync(connection, plant.Key);

            if (product == null)
            {
                anyUnknownSku = true;
                problems.Add(new { sku = plant.Key, error = "Product not found" });
            }
            else if (quantity <= 0)
                problems.Add(new { sku = product.Sku, error = "Quantity must be a positive number", requestedQuantity = quantity });

            // A courtesy at quote time, not a reservation: nothing here decrements QuantityOnHand,
            // so another conversation is free to buy the same stock before this customer comes
            // back to ConfirmOrder — which re-runs this exact comparison when it is the one that matters.
            else if (quantity > product.QuantityOnHand)
                problems.Add(new { sku = product.Sku, error = "Insufficient stock for the requested quantity", requestedQuantity = quantity, availableQuantity = product.QuantityOnHand });
            else
                quotedLines.Add((product, quantity));
        }

        if (problems.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No order was created: the lines below cannot be quoted",
                problems,
                availableSkus = anyUnknownSku ? await GetAllSkusAsync(connection) : null
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // getToolContext() (not a method parameter) is the only way to reach ConversationId: it
        // is the real Akka-assigned identifier, never exposed to or writable by the LLM. orderId +
        // sealWord together are the claim-check pair every later ConfirmOrder/CancelOrder must
        // present for the whole cart; sealWord is returned exactly once, right below and no Get*
        // tool in this class ever surfaces it again.
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

            for (int lineIndex = 0; lineIndex < quotedLines.Count; lineIndex++)
            {
                await using SqliteCommand insertLine = connection.CreateCommand();
                insertLine.Transaction = transaction;
                insertLine.CommandText = "INSERT INTO OrderLines (OrderId, LineNumber, Sku, Quantity) VALUES ($orderId, $lineNumber, $sku, $quantity)";
                insertLine.Parameters.AddWithValue("$orderId", orderId);
                insertLine.Parameters.AddWithValue("$lineNumber", lineIndex + 1);
                insertLine.Parameters.AddWithValue("$sku", quotedLines[lineIndex].Product.Sku);
                insertLine.Parameters.AddWithValue("$quantity", quotedLines[lineIndex].Quantity);
                await insertLine.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        toolLogger.LogInformation("Created purchase order {OrderId} with {LineCount} line(s) (user {CustomerCode})", orderId, quotedLines.Count, customerCode);

        return JsonSerializer.Serialize(new
        {
            orderId,
            sealWord,
            lines = quotedLines.Select(line => new
            {
                sku = line.Product.Sku,
                productName = line.Product.Name,
                quantity = line.Quantity,
                unitPrice = line.Product.UnitPrice,
                lineTotal = Math.Round(line.Product.UnitPrice * line.Quantity, 2)
            }),
            totalPrice = Math.Round(quotedLines.Sum(line => line.Product.UnitPrice * line.Quantity), 2),
            status = "Pending",
            note = "Order created but NOT committed: stock is untouched and nothing is billed until ConfirmOrder runs on this exact orderId and sealWord. One sealWord covers every line of this order. It appears in this response and in no other, now or later — ConfirmOrder and CancelOrder each require the pair, in this session and in any future one."
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Commits a Pending order: this is the only tool that actually decrements stock. Every line is
    /// committed or none is; availability is re-validated at commit time, since stock may have
    /// moved since the quote.
    /// </summary>
    /// <param name="orderId">Identifier of the order to confirm. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <returns>JSON object with the confirmed order, the remaining stock of each of its plants and the invoice it was billed to.</returns>
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
        Dictionary<string, Product?> productsBySku = new(StringComparer.OrdinalIgnoreCase);
        foreach (OrderLine line in order.Lines)
            productsBySku[line.Sku] = await FindProductAsync(connection, line.Sku);

        // Claiming the order (Pending -> Confirmed) and decrementing the stock of every line must
        // all happen or none: a single transaction. Its FIRST statement is a write, so it takes
        // the write lock straight away — no SELECT-then-UPDATE lock upgrade, hence none of SQLite's
        // classic writer-upgrade deadlock — while PRAGMA busy_timeout makes a losing concurrent
        // writer WAIT for this commit instead of throwing "database is locked".
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

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
                await transaction.RollbackAsync();
                return JsonSerializer.Serialize(new
                {
                    error = "Stock is no longer sufficient to confirm this order: nothing was confirmed",
                    orderId,
                    shortfalls = await FindShortfallsAsync(connection, order.Lines)
                }, GreenhouseDatabaseHelper.JsonOptions);
            }

            // Read back inside the SAME transaction, so the figure reported is the one just
            // written — not the possibly-stale pre-read value.
            await using SqliteCommand readStock = connection.CreateCommand();
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", line.Sku);
            long remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

            confirmedLines.Add(new { sku = line.Sku, quantity = line.Quantity, remainingStock });
        }

        // Bill every line, in the same transaction: a Confirmed order with no invoice line is exactly
        // the half-done state this method exists to prevent for stock. Every line lands on the same
        // invoice, the customer's open one for this month, which the first line found or opened.
        // A product deleted between the pre-reads and here confirms its line without billing it.
        string? invoiceId = null;
        foreach (OrderLine line in order.Lines)
        {
            if (productsBySku[line.Sku] is not { } product)
                continue;

            invoiceId = await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, order.CustomerCode!, product.Name,
                product.Sku, order.OrderId, product.UnitPrice, (int)line.Quantity, confirmedAtUtc);
        }

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

    // Read after the rollback, so each figure is the stock as it stands now: every line the order
    // can no longer be served for, not just the first one that failed the commit.
    private static async Task<List<object>> FindShortfallsAsync(SqliteConnection connection, IReadOnlyList<OrderLine> lines)
    {
        List<object> shortfalls = [];
        foreach (OrderLine line in lines)
        {
            Product? current = await FindProductAsync(connection, line.Sku);
            long availableQuantity = current?.QuantityOnHand ?? 0;
            if (availableQuantity < line.Quantity)
                shortfalls.Add(new { sku = line.Sku, requestedQuantity = line.Quantity, availableQuantity });
        }

        return shortfalls;
    }

    /// <summary>
    /// Retrieves the current status, lines and lifecycle timestamps of an existing order.
    /// </summary>
    /// <param name="orderId">Identifier of the order to inspect. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <returns>JSON object with order lines, status and timestamps.</returns>
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
    /// Cancels a Pending or Confirmed order as a whole. Restores the stock of every line only if the
    /// order had already been confirmed (a Pending order never touched stock in the first place).
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
            lines = order.Lines.Select(line => new { sku = line.Sku, quantity = line.Quantity }),
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

        List<object> orders = await FindOrderSummariesAsync(connection, "ConversationId = $conversationId", "$conversationId", ctx.ConversationId);

        return JsonSerializer.Serialize(new { totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists every order ever placed by a given customer, across ALL conversations/sessions —
    /// the full history behind a customerCode, not just the current chat. Summary only: sealWord is
    /// never included here (it is shown exactly once, by CreatePurchaseOrder), so seeing this
    /// list is not enough to act on any of the orders it names.
    /// </summary>
    /// <param name="customerCode">Identifier of the customer whose order history to retrieve (retrieved from shared context).</param>
    /// <returns>JSON array of that customer's orders with their lines, across every conversation (no sealWord included).</returns>
    public async Task<string> GetOrderHistory(string customerCode)
    {
        // customerCode is a shared context variable the LLM itself can write via SetContextVariable —
        // unlike GetOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access ConfirmOrder/CancelOrder do.
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        List<object> orders = await FindOrderSummariesAsync(connection, "CustomerCode = $customerCode COLLATE NOCASE", "$customerCode", customerCode);

        return JsonSerializer.Serialize(new { customerCode, customerName, totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }
}
