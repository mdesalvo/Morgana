using System.Globalization;
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

    private record Order(string OrderId, string Sku, long Quantity, string Status, string? CustomerCode, string? ConversationId, string SealWord, string CreatedAt, string? ConfirmedAt, string? CancelledAt);

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
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, CustomerCode, ConversationId, SealWord, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE OrderId = $orderId COLLATE NOCASE";
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
    /// <returns>The products with their stock status icons.</returns>
    public async Task<ProductCatalogResult> GetProductCatalog()
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Sku, Name, Category, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products ORDER BY Category, Name";

        List<CatalogProduct> products = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                long quantity = reader.GetInt64(3);
                long threshold = reader.GetInt64(4);

                products.Add(new CatalogProduct(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    quantity,
                    reader.GetDouble(5),
                    StockStatus(quantity, threshold),
                    StockStatusIcon(quantity, threshold)));
            }
        }

        return new ProductCatalogResult(products.Count, products);
    }

    /// <summary>
    /// Retrieves the detailed stock level for a single product.
    /// </summary>
    /// <param name="sku">Product SKU to inspect (e.g. "RSE-100").</param>
    /// <returns>The quantity, threshold, price and stock status, or the error listing the SKUs that exist.</returns>
    public async Task<StockLevelResult> CheckStockLevel(string sku)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Product? product = await FindProductAsync(connection, sku);
        if (product == null)
        {
            return new StockLevelResult(
                Error: "Product not found",
                RequestedSku: sku,
                AvailableSkus: await GetAllSkusAsync(connection));
        }

        return new StockLevelResult(
            Sku: product.Sku,
            Name: product.Name,
            Category: product.Category,
            QuantityOnHand: product.QuantityOnHand,
            ReorderThreshold: product.ReorderThreshold,
            UnitPrice: product.UnitPrice,
            StockStatus: StockStatus(product.QuantityOnHand, product.ReorderThreshold),
            StatusIcon: StockStatusIcon(product.QuantityOnHand, product.ReorderThreshold),
            MaxOrderableQuantity: product.QuantityOnHand);
    }

    /// <summary>
    /// Creates a new purchase order in "Pending" status. This is a QUOTE, not a commitment:
    /// stock is validated but NOT decremented here. The order only becomes binding once
    /// ConfirmOrder is called with the returned orderId AND sealWord.
    /// </summary>
    /// <param name="sku">Product SKU to order.</param>
    /// <param name="quantity">Quantity requested (must not exceed current stock).</param>
    /// <param name="customerCode">Identifier of the requesting customer (retrieved from shared context).</param>
    /// <returns>The new orderId, one-time sealWord, quote and pending status, or the error saying why no order was created.</returns>
    public async Task<PurchaseOrderResult> CreatePurchaseOrder(string sku, int quantity, string customerCode)
    {
        if (quantity <= 0)
            return new PurchaseOrderResult(Error: "Quantity must be a positive number", RequestedQuantity: quantity);

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Product? product = await FindProductAsync(connection, sku);
        if (product == null)
        {
            return new PurchaseOrderResult(
                Error: "Product not found",
                RequestedSku: sku,
                AvailableSkus: await GetAllSkusAsync(connection));
        }

        // This check is a courtesy at quote time, not a reservation: nothing here decrements
        // QuantityOnHand, so another conversation is free to buy the same stock between this
        // check and whenever (if ever) the customer comes back to actually ConfirmOrder — which
        // re-runs this exact comparison itself, right before it is the one that matters.
        if (quantity > product.QuantityOnHand)
        {
            return new PurchaseOrderResult(
                Error: "Insufficient stock for the requested quantity",
                Sku: product.Sku,
                RequestedQuantity: quantity,
                AvailableQuantity: product.QuantityOnHand);
        }

        // getToolContext() (not a method parameter) is the only way to reach ConversationId: it
        // is the real Akka-assigned identifier, never exposed to or writable by the LLM, so it is
        // trustworthy in a way a request/context parameter never could be — see ToolContext's
        // remarks in MorganaTool.cs. orderId + sealWord together are the claim-check pair every
        // later call to ConfirmOrder/CancelOrder must present; sealWord is returned
        // to the caller exactly once, right below and never stored anywhere the LLM can read it
        // back from later (no Get* tool in this class ever surfaces it again).
        ToolContext ctx = getToolContext();
        string orderId = $"ORD-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        string sealWord = GenerateSealWord();
        string createdAt = DateTime.UtcNow.ToString("O");

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Orders (OrderId, Sku, Quantity, Status, CustomerCode, ConversationId, SealWord, CreatedAt)
                VALUES ($orderId, $sku, $quantity, 'Pending', $customerCode, $conversationId, $sealWord, $createdAt)
                """;
            insert.Parameters.AddWithValue("$orderId", orderId);
            insert.Parameters.AddWithValue("$sku", product.Sku);
            insert.Parameters.AddWithValue("$quantity", quantity);
            insert.Parameters.AddWithValue("$customerCode", customerCode);
            insert.Parameters.AddWithValue("$conversationId", ctx.ConversationId);
            insert.Parameters.AddWithValue("$sealWord", sealWord);
            insert.Parameters.AddWithValue("$createdAt", createdAt);
            await insert.ExecuteNonQueryAsync();
        }

        toolLogger.LogInformation("Created purchase order {OrderId} for {Quantity}x {Sku} (user {CustomerCode})", orderId, quantity, sku, customerCode);

        return new PurchaseOrderResult(
            OrderId: orderId,
            SealWord: sealWord,
            Sku: product.Sku,
            ProductName: product.Name,
            Quantity: quantity,
            UnitPrice: product.UnitPrice,
            TotalPrice: Math.Round(product.UnitPrice * quantity, 2),
            Status: "Pending",
            Note: "Order created but NOT committed: stock is untouched and nothing is billed until ConfirmOrder runs on this exact orderId and sealWord. The sealWord appears in this response and in no other, now or later — ConfirmOrder and CancelOrder each require the pair, in this session and in any future one.");
    }

    /// <summary>
    /// Commits a Pending order: this is the only tool that actually decrements stock.
    /// Re-validates availability at commit time (stock may have moved since the quote).
    /// </summary>
    /// <param name="orderId">Identifier of the order to confirm. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <returns>The confirmed order and remaining stock, or the error saying why the order was not confirmed.</returns>
    public async Task<ConfirmOrderResult> ConfirmOrder(string orderId, string sealWord)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Order-not-found and wrong-sealWord return the IDENTICAL message on purpose: if a wrong
        // seal word got its own distinct error, that alone would confirm to the caller that the
        // orderId exists, turning this into a guessable oracle for enumerating real orders one
        // field at a time. One combined check, one combined message, no such leak.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return new ConfirmOrderResult(Error: "No order matches that orderId and sealWord combination", RequestedOrderId: orderId);

        // This pre-read is ONLY for the seal-word gate and a friendly fast-path error. It is NOT the
        // state the writes below trust: between here and the commit another conversation may
        // confirm/cancel this same order or drain the shared stock. Everything that must actually be
        // true for the commit to be legal is therefore re-asserted atomically INSIDE the transaction,
        // as a WHERE clause on the write itself — the pre-read is never the authority.
        if (order.Status != "Pending")
        {
            return new ConfirmOrderResult(
                Error: $"Order cannot be confirmed: current status is '{order.Status}', not 'Pending'",
                OrderId: orderId,
                Status: order.Status);
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
            return new ConfirmOrderResult(
                Error: $"Order cannot be confirmed: current status is '{latest?.Status ?? "Unknown"}', not 'Pending'",
                OrderId: orderId,
                Status: latest?.Status);
        }

        // Decrement stock, guarded so it can NEVER go negative: WHERE QuantityOnHand >= qty means a
        // concurrent confirmation that already took the last specimens leaves rows-affected at 0
        // here and we roll the whole thing back (undoing the claim above too) rather than commit a
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
            return new ConfirmOrderResult(
                Error: "Stock is no longer sufficient to confirm this order",
                OrderId: orderId,
                RequestedQuantity: order.Quantity,
                AvailableQuantity: current?.QuantityOnHand ?? 0);
        }

        // Read the remaining stock back inside the SAME transaction, so the figure reported is the
        // one we just wrote — not the possibly-stale pre-read value.
        long remainingStock;
        await using (SqliteCommand readStock = connection.CreateCommand())
        {
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", order.Sku);
            remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        // Bill it, in the same transaction: a Confirmed order with no invoice line is exactly the
        // half-done state this method exists to prevent for stock, so it must not exist for billing
        // either. product may be null only if the SKU was deleted between the pre-reads and here —
        // in that vanishingly unlikely case the order still confirms, just without a billed line.
        string? invoiceId = product == null
            ? null
            : await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, order.CustomerCode!, product.Name,
                order.Sku, order.OrderId, product.UnitPrice, (int)order.Quantity, confirmedAtUtc);

        await transaction.CommitAsync();

        toolLogger.LogInformation("Confirmed order {OrderId}: stock of {Sku} decremented by {Quantity}, billed to invoice {InvoiceId}", orderId, order.Sku, order.Quantity, invoiceId);

        return new ConfirmOrderResult(
            OrderId: orderId,
            Sku: order.Sku,
            Quantity: order.Quantity,
            Status: "Confirmed",
            ConfirmedAt: confirmedAt,
            RemainingStock: remainingStock,
            InvoiceId: invoiceId,
            Note: invoiceId == null
                ? null
                : "The order has been billed to this invoice as of this confirmation. This response carries the invoice identifier and nothing else about it: its total and its line items are not on these pages.");
    }

    /// <summary>
    /// Retrieves the current status and lifecycle timestamps of an existing order.
    /// </summary>
    /// <param name="orderId">Identifier of the order to inspect. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <returns>The order status and timestamps, or the error saying that no order matches.</returns>
    public async Task<OrderStatusResult> GetOrderStatus(string orderId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null)
            return new OrderStatusResult(Error: "No order matches that orderId", RequestedOrderId: orderId);

        return new OrderStatusResult(
            OrderId: order.OrderId,
            Sku: order.Sku,
            Quantity: order.Quantity,
            Status: order.Status,
            CreatedAt: order.CreatedAt,
            ConfirmedAt: order.ConfirmedAt,
            CancelledAt: order.CancelledAt);
    }

    /// <summary>
    /// Cancels a Pending or Confirmed order. Restores stock only if the order had already
    /// been confirmed (a Pending order never touched stock in the first place).
    /// </summary>
    /// <param name="orderId">Identifier of the order to cancel. Tracked from the conversation itself, NOT a single stored context value: a customer may have more than one order in flight.</param>
    /// <param name="sealWord">One-time seal word returned by CreatePurchaseOrder for this exact orderId. Tracked from the conversation itself, one per order — a customer with multiple orders in flight has a different seal word for each.</param>
    /// <param name="reason">Optional free-text cancellation reason, recorded for logging only.</param>
    /// <returns>The cancellation outcome, or the error saying why the order was not cancelled.</returns>
    public async Task<CancelOrderResult> CancelOrder(string orderId, string sealWord, string? reason = null)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // Same combined not-found/wrong-sealWord check as ConfirmOrder, same reason.
        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null || !string.Equals(order.SealWord, sealWord, StringComparison.OrdinalIgnoreCase))
            return new CancelOrderResult(Error: "No order matches that orderId and sealWord combination", RequestedOrderId: orderId);

        if (order.Status == "Cancelled")
            return new CancelOrderResult(Error: "Order is already cancelled", OrderId: orderId);

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
            return new CancelOrderResult(Error: "Order is already cancelled", OrderId: orderId);
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

        return new CancelOrderResult(
            OrderId: orderId,
            PreviousStatus: previousStatus,
            Status: "Cancelled",
            CancelledAt: cancelledAt,
            StockRestored: stockRestored,
            Reason: reason ?? "Not specified");
    }

    /// <summary>
    /// Lists the orders placed during THIS conversation, using the real Akka conversationId
    /// (never exposed to the LLM, never spoofable via a context variable) — no sealWord needed
    /// since the caller is, by construction, the same conversation that created them.
    /// </summary>
    /// <returns>This conversation's orders (no sealWord included).</returns>
    public async Task<OrdersResult> GetOrders()
    {
        // ctx.ConversationId, not a parameter: this scoping is intentionally NOT something the LLM
        // can influence or spoof — "this conversation's orders" means exactly that, decided by
        // Akka, not by whatever string a prompt-injected message might try to pass as an argument.
        ToolContext ctx = getToolContext();

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE ConversationId = $conversationId ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("$conversationId", ctx.ConversationId);

        List<OrderSummary> orders = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                orders.Add(new OrderSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        return new OrdersResult(orders.Count, orders);
    }

    /// <summary>
    /// Lists every order ever placed by a given customer, across ALL conversations/sessions —
    /// the full history behind a customerCode, not just the current chat. Summary only: sealWord is
    /// never included here (it is shown exactly once, by CreatePurchaseOrder), so seeing this
    /// list is not enough to act on any of the orders it names.
    /// </summary>
    /// <param name="customerCode">Identifier of the customer whose order history to retrieve (retrieved from shared context).</param>
    /// <returns>That customer's orders across every conversation (no sealWord included).</returns>
    public async Task<OrderHistoryResult> GetOrderHistory(string customerCode)
    {
        // customerCode is a shared context variable the LLM itself can write by passing it to a tool —
        // unlike GetOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access ConfirmOrder/CancelOrder do.
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OrderId, Sku, Quantity, Status, CreatedAt, ConfirmedAt, CancelledAt FROM Orders WHERE CustomerCode = $customerCode COLLATE NOCASE ORDER BY CreatedAt DESC";
        command.Parameters.AddWithValue("$customerCode", customerCode);

        List<OrderSummary> orders = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                orders.Add(new OrderSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        return new OrderHistoryResult(customerCode, customerName, orders.Count, orders);
    }
}