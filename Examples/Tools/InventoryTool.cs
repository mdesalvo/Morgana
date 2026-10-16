using System.ComponentModel;
using System.Globalization;
using Examples.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI;
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

    /// <summary>One row of an order read together with its lines: the order's header beside one line, or beside none.</summary>
    private record HeaderWithLine<THeader, TLine>(THeader Header, TLine? Line) where TLine : class;

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
        if (skuList.Count == 0)
            return productsBySku;

        await using SqliteCommand command = connection.CreateCommand();
        string placeholders = string.Join(", ", skuList.Select((_, index) => $"$sku{index}"));
        command.CommandText = $"SELECT Sku, Name, Category, Description, QuantityOnHand, ReorderThreshold, UnitPrice FROM Products WHERE Sku COLLATE NOCASE IN ({placeholders})";
        for (int index = 0; index < skuList.Count; index++)
            command.Parameters.AddWithValue($"$sku{index}", skuList[index]);

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

        // Every row carries the order's header beside one of its lines; the outer join gives a line-less row to an order with none.
        List<HeaderWithLine<Order, OrderLine>> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new HeaderWithLine<Order, OrderLine>(
                new Order(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    []),
                reader.IsDBNull(8) ? null : new OrderLine(reader.GetInt64(8), reader.GetString(9), reader.GetInt64(10))));

        // No row means no order under that identifier; otherwise the header is the first row's, holding every line.
        return rows.Count == 0
            ? null
            : rows[0].Header with { Lines = [.. rows.Select(row => row.Line).OfType<OrderLine>()] };
    }

    /// <summary>
    /// Lists the orders of one owner with their lines, reading headers and lines in a single query.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="ownerFilter">The condition on the orders that selects the owner, written over the alias <c>o</c> and the parameter <c>$owner</c>.</param>
    /// <param name="owner">The value of <c>$owner</c>.</param>
    private static async Task<List<OrderSummary>> FindOrderSummariesAsync(SqliteConnection connection, string ownerFilter, string owner)
    {
        // The most recent order first, its lines in line order; the order id keeps the rows of one order together.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT o.OrderId, o.Status, o.CreatedAt, o.ConfirmedAt, o.CancelledAt, l.Sku, l.Quantity
            FROM Orders o LEFT JOIN OrderLines l ON l.OrderId = o.OrderId
            WHERE {ownerFilter}
            ORDER BY o.CreatedAt DESC, o.OrderId, l.LineNumber
            """;
        command.Parameters.AddWithValue("$owner", owner);

        // Every row carries an order's header beside one of its lines; the outer join gives a line-less row to an order with none.
        List<HeaderWithLine<OrderSummary, OrderLineSummary>> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new HeaderWithLine<OrderSummary, OrderLineSummary>(
                new OrderSummary(
                    reader.GetString(0),
                    [],
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)),
                reader.IsDBNull(5) ? null : new OrderLineSummary(reader.GetString(5), reader.GetInt64(6))));

        // The rows of one order become its summary with every line, in the order the query sorted the orders.
        return [.. rows
            .GroupBy(row => row.Header.OrderId, StringComparer.Ordinal)
            .Select(orderRows => orderRows.First().Header with { Lines = [.. orderRows.Select(row => row.Line).OfType<OrderLineSummary>()] })];
    }

    /// <summary>
    /// Reports every line of an order whose stock no longer covers it, reading the stock of all the order's SKUs in one query.
    /// </summary>
    /// <remarks>Read after the rollback, so each figure is the stock as it stands now.</remarks>
    private static async Task<List<LineProblem>> FindShortfallsAsync(SqliteConnection connection, IReadOnlyList<OrderLine> lines)
    {
        Dictionary<string, Product> currentProducts = await FindProductsAsync(connection, lines.Select(line => line.Sku));

        // A line is short when its SKU is gone or holds fewer units than the order asks for.
        List<LineProblem> shortfalls = [];
        foreach (OrderLine line in lines)
        {
            long availableQuantity = currentProducts.TryGetValue(line.Sku, out Product? current) ? current.QuantityOnHand : 0;
            if (availableQuantity < line.Quantity)
                shortfalls.Add(new LineProblem(line.Sku, "Insufficient stock for the requested quantity", line.Quantity, availableQuantity));
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
    [Description("Retrieves the full greenhouse/nursery catalog with the stock status of every plant. This tool has only informative capabilities: it does not reserve, order or modify anything.")]
    [UserTool(1, "🌿 Browse the catalog")]
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
    [Description("Retrieves the current stock level of a single plant. An unknown sku comes back as an error listing the valid SKUs. This tool has only informative capabilities: it does not reserve, order or modify anything.")]
    [UserTool(2, "🌼 A specific plant or flower")]
    public async Task<StockLevelResult> CheckStockLevel(
        [Description("Product SKU to inspect (e.g. 'RSE-100'). Which plant is the user's choice: never guess it.")] [ToolParameter(Records.ToolScope.Request)] string sku)
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
    /// Creates one purchase order in "Pending" status, one line per plant of the cart. This is a
    /// QUOTE, not a commitment: stock is validated but NOT decremented here. The whole order only
    /// becomes binding once ConfirmOrder is called with the returned orderId AND its single sealWord.
    /// </summary>
    /// <param name="items">Every plant of the cart with its quantity; a plant named twice is merged into one line.</param>
    /// <param name="customerCode">Identifier of the requesting customer (retrieved from shared context).</param>
    /// <returns>The new orderId, one-time sealWord, quoted lines, total and pending status, or the lines that could not be quoted.</returns>
    [Description("Creates one purchase order in 'Pending' status for every plant of the cart and returns its quote. The cart is quoted whole or not at all: when any line cannot be quoted, no order is created and every such line is reported. This is a quote only: stock is validated but not decremented. The seal word is shown only this once; the customer needs it, together with the orderId, only to confirm or cancel the order from another conversation. It has dispositive potential only once ConfirmOrder is called afterwards — on its own it commits nothing.")]
    [SystemTool]
    public async Task<PurchaseOrderResult> CreatePurchaseOrder(
        [Description("Every plant of the order with its quantity, all in this one call: a plant named twice is merged into one line.")] [ToolParameter(Records.ToolScope.Request)] List<OrderItem> items,
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode)
    {
        // A cart with no plant has nothing to quote.
        if (items is not { Count: > 0 })
            return new PurchaseOrderResult(Error: "An order needs at least one plant");

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        // A plant named twice is one line: the model may split a quantity across entries.
        List<IGrouping<string, OrderItem>> plants = [.. items.GroupBy(item => item.Sku.Trim(), StringComparer.OrdinalIgnoreCase)];

        // One query finds every plant of the cart, so the lines below are judged without further reads.
        Dictionary<string, Product> productsBySku = await FindProductsAsync(connection, plants.Select(plant => plant.Key));

        // The cart is validated as a whole and either quoted whole or not at all: every problem of
        // every line is reported in one answer, so the customer fixes the cart once instead of
        // discovering its faults one rejected line at a time.
        List<QuotedLine> quotedLines = [];
        List<LineProblem> problems = [];
        foreach (IGrouping<string, OrderItem> plant in plants)
        {
            int quantity = plant.Sum(item => item.Quantity);

            // A SKU the shop does not stock cannot be quoted. The valid ones are listed below.
            if (!productsBySku.TryGetValue(plant.Key, out Product? product))
                problems.Add(new LineProblem(plant.Key, "Product not found"));
            // A quantity of zero or less is not a purchase.
            else if (quantity <= 0)
                problems.Add(new LineProblem(product.Sku, "Quantity must be a positive number", RequestedQuantity: quantity));
            // A courtesy at quote time, not a reservation: nothing here decrements QuantityOnHand,
            // so another conversation is free to buy the same stock before this customer comes
            // back to ConfirmOrder — which re-runs this exact comparison when it is the one that matters.
            else if (quantity > product.QuantityOnHand)
                problems.Add(new LineProblem(product.Sku, "Insufficient stock for the requested quantity", quantity, product.QuantityOnHand));
            // Everything else is a line of the quote.
            else
                quotedLines.Add(new QuotedLine(product.Sku, product.Name, quantity, product.UnitPrice, Math.Round(product.UnitPrice * quantity, 2)));
        }

        // One line that cannot be quoted keeps the whole cart from becoming an order.
        if (problems.Count > 0)
        {
            return new PurchaseOrderResult(
                Error: "No order was created: the lines below cannot be quoted",
                Problems: problems,
                // The valid SKUs are listed only when a line named one the shop does not stock.
                AvailableSkus: plants.Any(plant => !productsBySku.ContainsKey(plant.Key)) ? await GetAllSkusAsync(connection) : null);
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

            await transaction.CommitAsync();
        }

        toolLogger.LogInformation("Created purchase order {OrderId} with {LineCount} line(s) (user {CustomerCode})", orderId, quotedLines.Count, customerCode);

        return new PurchaseOrderResult(
            OrderId: orderId,
            SealWord: sealWord,
            Lines: quotedLines,
            TotalPrice: Math.Round(quotedLines.Sum(line => line.LineTotal), 2),
            Status: "Pending",
            Note: "Order created but not committed: stock is untouched and nothing is billed until ConfirmOrder runs on this exact orderId and sealWord. One sealWord covers every line of this order and appears in this response and in no other; the customer needs the pair only to confirm or cancel the order from another conversation.");
    }

    /// <summary>
    /// Commits a Pending order: decrements the stock of every line and bills the customer, all or none.
    /// </summary>
    /// <param name="orderId">Identifier of the order to confirm, as returned by CreatePurchaseOrder.</param>
    /// <param name="sealWord">One-time seal word from CreatePurchaseOrder for this exact orderId.</param>
    /// <returns>The confirmed lines with their remaining stock and the invoiceId; the error instead when the order was not confirmed.</returns>
    [Description("Commits a Pending order, all its lines or none: the only one of your tools that decrements stock and the only one that bills the customer, one invoice line per order line. Requires the exact orderId and sealWord pair returned by CreatePurchaseOrder — a mismatch on either is reported identically as 'no order matches', to avoid confirming or denying whether an orderId exists. Re-validates the availability of every line at commit time. It returns the invoice's identifier and nothing else about it. This tool has dispositive capabilities: it commits a real, persistent change to greenhouse stock and to the customer's account.")]
    [SystemTool]
    [RequiresApproval]
    public async Task<ConfirmOrderResult> ConfirmOrder(
        [Description("Identifier of the Pending order to confirm, exactly as returned by CreatePurchaseOrder.")] [ToolParameter(Records.ToolScope.Request)] string orderId,
        [Description("One-time seal word returned by CreatePurchaseOrder alongside this exact orderId. If the customer does not have it, tell them plainly you need the seal word they were given when that specific order was created — never ask for a plausible-sounding guess.")] [ToolParameter(Records.ToolScope.Request)] string sealWord)
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

        // Read once, outside the transaction, purely for the name/price that go on each invoice
        // line below — the same tolerance CreatePurchaseOrder already applies to its own quote.
        Dictionary<string, Product> productsBySku = await FindProductsAsync(connection, order.Lines.Select(line => line.Sku));

        // Claiming the order (Pending -> Confirmed) and decrementing the stock of every line must
        // all happen or none: a single transaction. Its FIRST statement is a write, so it takes
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

        // Decrement each line's stock, guarded so it can NEVER go negative: a concurrent
        // confirmation that already took the last specimens of any one plant leaves rows-affected
        // at 0 and the whole order rolls back (undoing the claim and every earlier line too) rather
        // than commit a cart that is only partly in stock. This guard, not the quote, is the truthful one.
        List<ConfirmedLine> confirmedLines = [];
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
                return new ConfirmOrderResult(
                    Error: "Stock is no longer sufficient to confirm this order: nothing was confirmed",
                    OrderId: orderId,
                    Shortfalls: await FindShortfallsAsync(connection, order.Lines));
            }

            // Read back inside the SAME transaction, so the figure reported is the one just
            // written — not the possibly-stale pre-read value.
            await using SqliteCommand readStock = connection.CreateCommand();
            readStock.Transaction = transaction;
            readStock.CommandText = "SELECT QuantityOnHand FROM Products WHERE Sku = $sku";
            readStock.Parameters.AddWithValue("$sku", line.Sku);
            long remainingStock = Convert.ToInt64(await readStock.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

            confirmedLines.Add(new ConfirmedLine(line.Sku, line.Quantity, remainingStock));
        }

        // Bill every line, in the same transaction: a Confirmed order with no invoice line is exactly
        // the half-done state this method exists to prevent for stock. Every line lands on the same
        // invoice, the customer's open one for this month, which the first line found or opened.
        // A product deleted between the pre-reads and here confirms its line without billing it.
        string? invoiceId = null;
        foreach (OrderLine line in order.Lines)
        {
            if (!productsBySku.TryGetValue(line.Sku, out Product? product))
                continue;

            invoiceId = await GreenhouseDatabaseHelper.BillCustomerAsync(connection, transaction, order.CustomerCode!, product.Name,
                product.Sku, order.OrderId, product.UnitPrice, (int)line.Quantity, confirmedAtUtc);
        }

        await transaction.CommitAsync();

        toolLogger.LogInformation("Confirmed order {OrderId}: stock of {LineCount} line(s) decremented, billed to invoice {InvoiceId}", orderId, order.Lines.Count, invoiceId);

        return new ConfirmOrderResult(
            OrderId: orderId,
            Lines: confirmedLines,
            Status: "Confirmed",
            ConfirmedAt: confirmedAt,
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
    [Description("Retrieves the current status and lifecycle timestamps of an existing order. Keyed on the orderId alone: no seal word is required, because reading a status commits nothing. An orderId nothing is filed under is reported as 'no order matches'. This tool has only informative capabilities: it does not modify the order.")]
    [UserTool(5, "📍 An order's status")]
    public async Task<OrderStatusResult> GetOrderStatus(
        [Description("Identifier of the order to inspect, as originally returned by CreatePurchaseOrder. May belong to a past session — always trust this tool's answer over what you remember.")] [ToolParameter(Records.ToolScope.Request)] string orderId)
    {
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        Order? order = await FindOrderAsync(connection, orderId);
        if (order == null)
            return new OrderStatusResult(Error: "No order matches that orderId", RequestedOrderId: orderId);

        return new OrderStatusResult(
            OrderId: order.OrderId,
            Lines: [.. order.Lines.Select(line => new OrderLineSummary(line.Sku, line.Quantity))],
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
    [Description("Cancels a Pending or Confirmed order. Requires the exact orderId and sealWord pair returned by CreatePurchaseOrder — a mismatch on either is reported identically as 'no order matches'. If the order had been Confirmed, the stock of every line is restored; a Pending order never touched stock so none is restored. This tool has dispositive capabilities: it commits a real, persistent change to an order and possibly to greenhouse stock.")]
    [SystemTool]
    [RequiresApproval]
    public async Task<CancelOrderResult> CancelOrder(
        [Description("Identifier of the order to cancel, exactly as returned by CreatePurchaseOrder or previously discussed.")] [ToolParameter(Records.ToolScope.Request)] string orderId,
        [Description("One-time seal word returned by CreatePurchaseOrder for this exact orderId.")] [ToolParameter(Records.ToolScope.Request)] string sealWord,
        [Description("Optional reason for the cancellation, when the user gave one (e.g., 'changed my mind', 'found a better price'). Recorded for internal purposes.")] [ToolParameter(Records.ToolScope.Request)] string? reason = null)
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

        return new CancelOrderResult(
            OrderId: orderId,
            PreviousStatus: previousStatus,
            Status: "Cancelled",
            CancelledAt: cancelledAt,
            StockRestored: stockRestored,
            Reason: reason ?? "Not specified");
    }

    /// <summary>
    /// Lists the orders placed during this conversation.
    /// </summary>
    /// <returns>This conversation's orders (without sealWords).</returns>
    [Description("Lists the orders placed during this conversation. It never carries a seal word (shown only once, by CreatePurchaseOrder), so this listing alone is never enough to confirm or cancel anything. This tool has only informative capabilities.")]
    [SystemTool]
    public async Task<OrdersResult> GetConversationOrders()
    {
        // ctx.ConversationId, not a parameter: this scoping is intentionally NOT something the LLM
        // can influence or spoof — "this conversation's orders" means exactly that, decided by
        // Akka, not by whatever string a prompt-injected message might try to pass as an argument.
        ToolContext ctx = getToolContext();

        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        List<OrderSummary> orders = await FindOrderSummariesAsync(connection, "o.ConversationId = $owner", ctx.ConversationId);

        return new OrdersResult(orders.Count, orders);
    }

    /// <summary>
    /// Lists every order a customer has placed across all conversations and sessions.
    /// </summary>
    /// <param name="customerCode">Customer code (retrieved from shared context).</param>
    /// <returns>That customer's order history across all conversations (without sealWords).</returns>
    [Description("Lists every order a customer has ever placed, across all conversations and sessions — not just this one. It never carries a seal word (shown only once, by CreatePurchaseOrder), so this listing alone is never enough to confirm or cancel any order it names — that pair is what ConfirmOrder and CancelOrder require. To re-read one single order by its identifier, use GetOrderStatus. This tool has only informative capabilities.")]
    [UserTool(4, "📦 My orders")]
    public async Task<OrderHistoryResult> GetOrderHistory(
        [Description("The customer's own identifying code, whatever they call it — customer code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one code. It scopes the listing to that one customer and is not a selector for choosing whose history to read.")] [ToolParameter(Records.ToolScope.Context, shared: true)] string customerCode)
    {
        // customerCode is a shared context variable the LLM itself can write by passing it to a tool —
        // unlike GetConversationOrders()'s ConversationId, it is not a trust boundary, which is exactly why
        // this tool deliberately stops at a summary (no sealWord, no ability to act on any
        // of these orders) rather than granting the same access ConfirmOrder/CancelOrder do.
        await using SqliteConnection connection = await GreenhouseDatabaseHelper.OpenConnectionAsync();

        string? customerName = await FindCustomerNameAsync(connection, customerCode);

        List<OrderSummary> orders = await FindOrderSummariesAsync(connection, "o.CustomerCode = $owner COLLATE NOCASE", customerCode);

        return new OrderHistoryResult(customerCode, customerName, orders.Count, orders);
    }
}