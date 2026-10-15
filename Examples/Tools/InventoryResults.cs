using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="InventoryTool.GetProductCatalog"/> returns: every plant of the catalog with its stock status.</summary>
[Description("Every plant of the catalog with its stock status.")]
public record ProductCatalogResult(
    [Description("How many products the catalog holds.")] int TotalProducts,
    [Description("The products of the catalog, grouped by category.")] List<CatalogProduct> Products);

/// <summary>One plant in the catalog listing.</summary>
[Description("One plant of the catalog.")]
public record CatalogProduct(
    [Description("The stock-keeping unit that identifies the plant.")] string Sku,
    [Description("The name of the plant.")] string Name,
    [Description("The category of the plant: Flowering Shrubs, Exotic Flowers, Aromatic Herbs, Seeds & Bulbs, Foliage Plants or Succulents & Cacti.")] string Category,
    [Description("How many units are in stock.")] long QuantityOnHand,
    [Description("The price of one unit in euros.")] double UnitPrice,
    [Description("The stock status: InStock, LowStock or OutOfStock.")] string StockStatus,
    [Description("The icon that goes with the stock status.")] string StatusIcon);

/// <summary>What <see cref="InventoryTool.CheckStockLevel"/> returns: the stock of one product, or the error listing the SKUs that exist.</summary>
[Description("The stock of one plant.")]
public record StockLevelResult(
    [Description("Why no stock level is returned; present only when the product was not found.")] string? Error = null,
    [Description("The stock-keeping unit that identifies the plant.")] string? Sku = null,
    [Description("The name of the plant.")] string? Name = null,
    [Description("The category of the plant: Flowering Shrubs, Exotic Flowers, Aromatic Herbs, Seeds & Bulbs, Foliage Plants or Succulents & Cacti.")] string? Category = null,
    [Description("How many units are in stock.")] long? QuantityOnHand = null,
    [Description("The quantity at or under which the stock counts as low.")] long? ReorderThreshold = null,
    [Description("The price of one unit in euros.")] double? UnitPrice = null,
    [Description("The stock status: InStock, LowStock or OutOfStock.")] string? StockStatus = null,
    [Description("The icon that goes with the stock status.")] string? StatusIcon = null,
    [Description("The largest quantity that can be ordered, which is the stock on hand.")] long? MaxOrderableQuantity = null,
    [Description("The SKU that was asked for; present only on an error.")] string? RequestedSku = null,
    [Description("The SKUs that exist; present only on an error.")] List<string>? AvailableSkus = null);

/// <summary>What <see cref="InventoryTool.CreatePurchaseOrder"/> returns: the quote just created, or the error saying why none was.</summary>
[Description("The quote just created for a plant.")]
public record PurchaseOrderResult(
    [Description("Why no order was created; null when one was.")] string? Error = null,
    [Description("The identifier of the new order.")] string? OrderId = null,
    [Description("The word, shown this one time only, that confirming or cancelling the order requires with its identifier.")] string? SealWord = null,
    [Description("The stock-keeping unit that identifies the plant.")] string? Sku = null,
    [Description("The name of the plant.")] string? ProductName = null,
    [Description("How many units are quoted.")] int? Quantity = null,
    [Description("The price of one unit in euros.")] double? UnitPrice = null,
    [Description("The price of the whole quantity in euros.")] double? TotalPrice = null,
    [Description("The status of the new order, which is Pending.")] string? Status = null,
    [Description("States what this response does and does not carry.")] string? Note = null,
    [Description("The SKU that was asked for; present only on an error.")] string? RequestedSku = null,
    [Description("The SKUs that exist; present only on an error.")] List<string>? AvailableSkus = null,
    [Description("The quantity that was asked for; present only on an error.")] int? RequestedQuantity = null,
    [Description("The quantity in stock; present only on an error.")] long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.ConfirmOrder"/> returns: the order just confirmed, or the error saying why it was not.</summary>
[Description("The order just confirmed.")]
public record ConfirmOrderResult(
    [Description("Why the order was not confirmed; null when it was.")] string? Error = null,
    [Description("The order identifier that was asked for; present only on an error.")] string? RequestedOrderId = null,
    [Description("The identifier of the order.")] string? OrderId = null,
    [Description("The stock-keeping unit that identifies the plant.")] string? Sku = null,
    [Description("How many units the order commits.")] long? Quantity = null,
    [Description("The status of the order, which is Confirmed.")] string? Status = null,
    [Description("The moment the order was confirmed, as an ISO 8601 UTC timestamp.")] string? ConfirmedAt = null,
    [Description("The units left in stock after the confirmation.")] long? RemainingStock = null,
    [Description("The invoice that the order was billed to.")] string? InvoiceId = null,
    [Description("States what this response does and does not carry.")] string? Note = null,
    [Description("The quantity that the order asks for; present only on an error.")] long? RequestedQuantity = null,
    [Description("The quantity in stock; present only on an error.")] long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.GetOrderStatus"/> returns: the status and timestamps of an order, or the error saying that none matches.</summary>
[Description("Where an order stands and when it got there.")]
public record OrderStatusResult(
    [Description("Why no status is returned; present only when no order matches.")] string? Error = null,
    [Description("The identifier of the order.")] string? OrderId = null,
    [Description("The stock-keeping unit that identifies the plant.")] string? Sku = null,
    [Description("How many units the order covers.")] long? Quantity = null,
    [Description("The status of the order: Pending, Confirmed or Cancelled.")] string? Status = null,
    [Description("The moment the order was created, as an ISO 8601 UTC timestamp.")] string? CreatedAt = null,
    [Description("The moment the order was confirmed, as an ISO 8601 UTC timestamp; null until then.")] string? ConfirmedAt = null,
    [Description("The moment the order was cancelled, as an ISO 8601 UTC timestamp; null until then.")] string? CancelledAt = null,
    [Description("The order identifier that was asked for; present only on an error.")] string? RequestedOrderId = null);

/// <summary>What <see cref="InventoryTool.CancelOrder"/> returns: the cancellation just made, or the error saying why it was not.</summary>
[Description("The cancellation just made.")]
public record CancelOrderResult(
    [Description("Why the order was not cancelled; null when it was.")] string? Error = null,
    [Description("The order identifier that was asked for; present only on an error.")] string? RequestedOrderId = null,
    [Description("The identifier of the order.")] string? OrderId = null,
    [Description("The status the order had before the cancellation: Pending or Confirmed.")] string? PreviousStatus = null,
    [Description("The status of the order, which is Cancelled.")] string? Status = null,
    [Description("The moment the order was cancelled, as an ISO 8601 UTC timestamp.")] string? CancelledAt = null,
    [Description("True when the stock of a confirmed order was given back; false for a pending order, which never took any.")] bool? StockRestored = null,
    [Description("The reason for the cancellation that was given; Not specified when none was.")] string? Reason = null);

/// <summary>One order in a listing.</summary>
/// <remarks>The confirmation and cancellation times are written even when null, since a null says that the order never reached that step.</remarks>
[Description("One order in a listing.")]
public record OrderSummary(
    [Description("The identifier of the order.")] string OrderId,
    [Description("The stock-keeping unit that identifies the plant.")] string Sku,
    [Description("How many units the order covers.")] long Quantity,
    [Description("The status of the order: Pending, Confirmed or Cancelled.")] string Status,
    [Description("The moment the order was created, as an ISO 8601 UTC timestamp.")] string CreatedAt,
    [Description("The moment the order was confirmed, as an ISO 8601 UTC timestamp; null when it never was.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConfirmedAt,
    [Description("The moment the order was cancelled, as an ISO 8601 UTC timestamp; null when it never was.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CancelledAt);

/// <summary>What <see cref="InventoryTool.GetOrders"/> returns: the orders placed during the current conversation.</summary>
[Description("The orders placed during one conversation.")]
public record OrdersResult(
    [Description("How many orders this conversation placed.")] int TotalOrders,
    [Description("The orders, most recent first.")] List<OrderSummary> Orders);

/// <summary>What <see cref="InventoryTool.GetOrderHistory"/> returns: every order ever placed under a customer code.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
[Description("Every order that a customer ever placed.")]
public record OrderHistoryResult(
    [Description("The customer code the books were asked about.")] string CustomerCode,
    [Description("The customer's name; null when the shop does not know the code.")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("How many orders the customer ever placed.")] int TotalOrders,
    [Description("The orders, most recent first.")] List<OrderSummary> Orders);
