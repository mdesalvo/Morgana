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

    private record Order(string OrderId, string Sku, long Quantity, string Status, string? UserId, string? ConversationId, string SealWord, string CreatedAt, string? ConfirmedAt, string? CancelledAt);

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
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, UserId, ConversationId, SealWord, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE OrderId = $orderId COLLATE NOCASE";
        command.Parameters.AddWithValue("$orderId", orderId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new Order(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
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

    private static async Task<string?> FindCustomerNameAsync(SqliteConnection connection, string userId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DisplayName FROM Customers WHERE UserId = $userId COLLATE NOCASE";
        command.Parameters.AddWithValue("$userId", userId);

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
    /// Creates a new purchase order in "Pending" status. This is a QUOTE, not a commitment:
    /// stock is validated but NOT decremented here. The order only becomes binding once
    /// ConfirmOrder is called with the returned orderId AND sealWord.
    /// </summary>
    /// <param name="sku">Product SKU to order.</param>
    /// <param name="quantity">Quantity requested (must not exceed current stock).</param>
    /// <param name="userId">Identifier of the requesting customer (retrieved from shared context).</param>
    /// <returns>JSON object with the new orderId, one-time sealWord, quote and pending status.</returns>
    public async Task<string> CreatePurchaseOrder(string sku, int quantity, string userId)
    {
        if (quantity <= 0)
            return JsonSerializer.Serialize(new { error = "Quantity must be a positive number", requestedQuantity = quantity }, GreenhouseDatabaseHelper.JsonOptions);

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

        // This check is a courtesy at quote time, not a reservation: nothing here decrements
        // QuantityOnHand, so another conversation is free to buy the same stock between this
        // check and whenever (if ever) the customer comes back to actually ConfirmOrder — which
        // re-runs this exact comparison itself, right before it is the one that matters.
        if (quantity > product.QuantityOnHand)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Insufficient stock for the requested quantity",
                sku = product.Sku,
                requestedQuantity = quantity,
                availableQuantity = product.QuantityOnHand
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // getToolContext() (not a method parameter) is the only way to reach ConversationId: it
        // is the real Akka-assigned identifier, never exposed to or writable by the LLM, so it is
        // trustworthy in a way a request/context parameter never could be — see ToolContext's
        // remarks in MorganaTool.cs. orderId + sealWord together are the claim-check pair every
        // later call to ConfirmOrder/CancelOrder/GetOrderStatus must present; sealWord is returned
        // to the caller exactly once, right below, and never stored anywhere the LLM can read it
        // back from later (no Get* tool in this class ever surfaces it again).
        ToolContext ctx = getToolContext();
        string orderId = $"ORD-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        string sealWord = GenerateSealWord();
        string createdAt = DateTime.UtcNow.ToString("O");

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Orders (OrderId, Sku, Quantity, Status, UserId, ConversationId, SealWord, CreatedAt)
                VALUES ($orderId, $sku, $quantity, 'Pending', $userId, $conversationId, $sealWord, $createdAt)
                """;
            insert.Parameters.AddWithValue("$orderId", orderId);
            insert.Parameters.AddWithValue("$sku", product.Sku);
            insert.Parameters.AddWithValue("$quantity", quantity);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$conversationId", ctx.ConversationId);
            insert.Parameters.AddWithValue("$sealWord", sealWord);
            insert.Parameters.AddWithValue("$createdAt", createdAt);
            await insert.ExecuteNonQueryAsync();
        }

        toolLogger.LogInformation("Created purchase order {OrderId} for {Quantity}x {Sku} (user {UserId})", orderId, quantity, sku, userId);

        return JsonSerializer.Serialize(new
        {
            orderId,
            sealWord,
            sku = product.Sku,
            productName = product.Name,
            quantity,
            unitPrice = product.UnitPrice,
            totalPrice = Math.Round(product.UnitPrice * quantity, 2),
            status = "Pending",
            note = "Order created but NOT yet committed: stock has not been touched. The sealWord is shown ONLY this once — tell the user to keep both orderId and sealWord, they are required together for ConfirmOrder, CancelOrder and GetOrderStatus, even in a future session. Call ConfirmOrder with this exact orderId and sealWord ONLY after the user has explicitly confirmed they want to proceed."
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Commits a Pending order: this is the only tool that actually decrements stock.
    /// Re-validates availability at commit time (stock may have moved since the quote).
    /// </summary>
    /// <param name="orderId">Identifier of the order to confirm. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <returns>JSON object with the confirmed order and remaining stock.</returns>
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

        // Read once, outside the transaction, purely for the name/price that go on the invoice
        // line below — the same tolerance CreatePurchaseOrder already applies to its own quote.
        Product? product = await FindProductAsync(connection, order.Sku);

        // Claiming the order (Pending -> Confirmed) and decrementing stock must both happen or
        // neither: a single transaction. The transaction's FIRST statement is a write, so it takes
        // the write lock straight away — no SELECT-then-UPDATE lock upgrade, hence none of SQLite's
        // classic writer-upgrade deadlock — while PRAGMA busy_timeout (set by GreenhouseDatabaseHelper.OpenConnectionAsync)
        // makes a losing concurrent writer WAIT for our commit instead of throwing "database is locked".
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

        // Decrement stock, guarded so it can NEVER go negative: WHERE QuantityOnHand >= qty means a
        // concurrent confirmation that already took the last specimens leaves rows-affected at 0
        // here, and we roll the whole thing back (undoing the claim above too) rather than commit a
        // sale of stock that no longer exists. This guard, not the pre-quote check, is the truthful one.
        int stockRows;
        await using (SqliteCommand updateStock = connection.CreateCommand())
        {
            updateStock.Transaction = transaction;
            updateStock.CommandText = "UPDATE Products SET QuantityOnHand = QuantityOnHand - $qty WHERE Sku = $sku AND QuantityOnHand >= $qty";
            updateStock.Parameters.AddWithValue("$qty", order.Quantity);
            updateStock.Parameters.AddWithValue("$sku", order.Sku);
            stockRows = await updateStock.ExecuteNonQueryAsync();
        }

        if (stockRows == 0)
        {
            await transaction.RollbackAsync();
            Product? current = await FindProductAsync(connection, order.Sku);
            return JsonSerializer.Serialize(new
            {
                error = "Stock is no longer sufficient to confirm this order",
                orderId,
                requestedQuantity = order.Quantity,
                availableQuantity = current?.QuantityOnHand ?? 0
            }, GreenhouseDatabaseHelper.JsonOptions);
        }

        // Read the remaining stock back inside the SAME transaction, so the figure reported is the
        // one we just wrote — not the possibly-stale pre-read value.
        long remainingStock;
        await using (SqliteCommand readStock = connection.CreateCommand())
        {
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", order.Sku);
            remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync());
        }

        // Bill it, in the same transaction: a Confirmed order with no invoice line is exactly the
        // half-done state this method exists to prevent for stock, so it must not exist for billing
        // either. product may be null only if the SKU was deleted between the pre-reads and here —
        // in that vanishingly unlikely case the order still confirms, just without a billed line.
        string? invoiceId = product == null
            ? null
            : await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, order.UserId!, product.Name,
                order.Sku, order.OrderId, product.UnitPrice, (int)order.Quantity, confirmedAtUtc);

        await transaction.CommitAsync();

        toolLogger.LogInformation("Confirmed order {OrderId}: stock of {Sku} decremented by {Quantity}, billed to invoice {InvoiceId}", orderId, order.Sku, order.Quantity, invoiceId);

        return JsonSerializer.Serialize(new
        {
            orderId,
            sku = order.Sku,
            quantity = order.Quantity,
            status = "Confirmed",
            confirmedAt,
            remainingStock,
            invoiceId,
            note = invoiceId == null
                ? null
                : "The order has been billed to this invoice as of this confirmation — tell the customer, in character, that their order is settled and that they can ask Morgana (or the accounts desk) to see this invoice, now or later. Never read out invoice totals or line items yourself: that belongs to the accounts desk."
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Retrieves the current status and lifecycle timestamps of an existing order.
    /// </summary>
    /// <param name="orderId">Identifier of the order to inspect. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <returns>JSON object with order status and timestamps.</returns>
    public async Task<string> GetOrderStatus(string orderId, string sealWord)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Same combined not-found/wrong-sealWord check as ConfirmOrder, same reason: a distinct
        // "wrong seal word" message would confirm the orderId is real even when it isn't the
        // caller's to inspect.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { error = "No order matches that orderId and sealWord combination", requestedOrderId = orderId }, GreenhouseDatabaseHelper.JsonOptions);

        return JsonSerializer.Serialize(new
        {
            orderId = order.OrderId,
            sku = order.Sku,
            quantity = order.Quantity,
            status = order.Status,
            createdAt = order.CreatedAt,
            confirmedAt = order.ConfirmedAt,
            cancelledAt = order.CancelledAt
        }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Cancels a Pending or Confirmed order. Restores stock only if the order had already
    /// been confirmed (a Pending order never touched stock in the first place).
    /// </summary>
    /// <param name="orderId">Identifier of the order to cancel. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <param name="reason">Optional free-text cancellation reason, recorded for logging only.</param>
    /// <returns>JSON object describing the cancellation outcome.</returns>
    public async Task<string> CancelOrder(string orderId, string sealWord, string? reason = null)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Same combined not-found/wrong-sealWord check as ConfirmOrder/GetOrderStatus, same reason.
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
        // restores stock, and only one caller can ever win it.
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
            await using SqliteCommand restoreStock = connection.CreateCommand();
            restoreStock.Transaction = transaction;
            restoreStock.CommandText = "UPDATE Products SET QuantityOnHand = QuantityOnHand + $qty WHERE Sku = $sku";
            restoreStock.Parameters.AddWithValue("$qty", order.Quantity);
            restoreStock.Parameters.AddWithValue("$sku", order.Sku);
            await restoreStock.ExecuteNonQueryAsync();
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
    /// <returns>JSON array of this conversation's orders (no sealWord included).</returns>
    public async Task<string> GetOrders()
    {
        // ctx.ConversationId, not a parameter: this scoping is intentionally NOT something the LLM
        // can influence or spoof — "this conversation's orders" means exactly that, decided by
        // Akka, not by whatever string a prompt-injected message might try to pass as an argument.
        ToolContext ctx = getToolContext();

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE ConversationId = $conversationId ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("$conversationId", ctx.ConversationId);

        List<object> orders = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                orders.Add(new
                {
                    orderId = reader.GetString(0),
                    sku = reader.GetString(1),
                    quantity = reader.GetInt64(2),
                    status = reader.GetString(3),
                    createdAt = reader.GetString(4),
                    confirmedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    cancelledAt = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }

        return JsonSerializer.Serialize(new { totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }

    /// <summary>
    /// Lists every order ever placed by a given customer, across ALL conversations/sessions —
    /// the full history behind a userId, not just the current chat. Summary only: sealWord is
    /// never included here (it is shown exactly once, by CreatePurchaseOrder), so seeing this
    /// list is not enough to act on or fully inspect any specific past order.
    /// </summary>
    /// <param name="userId">Identifier of the customer whose order history to retrieve (retrieved from shared context).</param>
    /// <returns>JSON array of that customer's orders across every conversation (no sealWord included).</returns>
    public async Task<string> GetOrderHistory(string userId)
    {
        // userId is a shared context variable the LLM itself can write via SetContextVariable —
        // unlike GetOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access GetOrderStatus/ConfirmOrder do.
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, userId);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE UserId = $userId COLLATE NOCASE ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("$userId", userId);

        List<object> orders = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                orders.Add(new
                {
                    orderId = reader.GetString(0),
                    sku = reader.GetString(1),
                    quantity = reader.GetInt64(2),
                    status = reader.GetString(3),
                    createdAt = reader.GetString(4),
                    confirmedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    cancelledAt = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }

        return JsonSerializer.Serialize(new { userId, customerName, totalOrders = orders.Count, orders }, GreenhouseDatabaseHelper.JsonOptions);
    }
}