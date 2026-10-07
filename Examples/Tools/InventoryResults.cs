using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="InventoryTool.GetProductCatalog"/> returns: every plant of the catalog with its stock status.</summary>
public record ProductCatalogResult(
    [Description("How many products the catalog holds")] int TotalProducts,
    [Description("The products with SKU, name, category, quantity, price and stock status")] List<CatalogProduct> Products);

/// <summary>One plant in the catalog listing.</summary>
public record CatalogProduct(
    string Sku,
    string Name,
    string Category,
    long QuantityOnHand,
    double UnitPrice,
    string StockStatus,
    string StatusIcon);

/// <summary>What <see cref="InventoryTool.CheckStockLevel"/> returns: the stock of one product, or the error listing the SKUs that exist.</summary>
public record StockLevelResult(
    [Description("Why no stock level is returned, present only when the product was not found")] string? Error = null,
    [Description("The product SKU")] string? Sku = null,
    [Description("The product name")] string? Name = null,
    [Description("The product category")] string? Category = null,
    [Description("The quantity in stock")] long? QuantityOnHand = null,
    [Description("The quantity at or under which stock is low")] long? ReorderThreshold = null,
    [Description("The price of one unit")] double? UnitPrice = null,
    [Description("InStock, LowStock or OutOfStock")] string? StockStatus = null,
    [Description("The icon of the stock status")] string? StatusIcon = null,
    [Description("The largest quantity that can be ordered")] long? MaxOrderableQuantity = null,
    [Description("The SKU that was asked for, on an error")] string? RequestedSku = null,
    [Description("The SKUs that exist, on an error")] List<string>? AvailableSkus = null);

/// <summary>What <see cref="InventoryTool.CreatePurchaseOrder"/> returns: the quote just created, or the error saying why none was.</summary>
public record PurchaseOrderResult(
    [Description("Why no order was created")] string? Error = null,
    [Description("The new order identifier")] string? OrderId = null,
    [Description("The one-time word that confirming or cancelling the order requires")] string? SealWord = null,
    [Description("The product SKU")] string? Sku = null,
    [Description("The product name")] string? ProductName = null,
    [Description("The quantity quoted")] int? Quantity = null,
    [Description("The price of one unit")] double? UnitPrice = null,
    [Description("The price of the whole quantity")] double? TotalPrice = null,
    [Description("The status of the order")] string? Status = null,
    [Description("What the response does and does not carry")] string? Note = null,
    [Description("The SKU that was asked for, on an error")] string? RequestedSku = null,
    [Description("The SKUs that exist, on an error")] List<string>? AvailableSkus = null,
    [Description("The quantity that was asked for, on an error")] int? RequestedQuantity = null,
    [Description("The quantity in stock, on an error")] long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.ConfirmOrder"/> returns: the order just confirmed, or the error saying why it was not.</summary>
public record ConfirmOrderResult(
    [Description("Why the order was not confirmed")] string? Error = null,
    [Description("The order identifier that was asked for, on an error")] string? RequestedOrderId = null,
    [Description("The order identifier")] string? OrderId = null,
    [Description("The product SKU")] string? Sku = null,
    [Description("The quantity confirmed")] long? Quantity = null,
    [Description("The status of the order")] string? Status = null,
    [Description("When the order was confirmed")] string? ConfirmedAt = null,
    [Description("The stock left after the confirmation")] long? RemainingStock = null,
    [Description("The invoice the order was billed to")] string? InvoiceId = null,
    [Description("What the response does and does not carry")] string? Note = null,
    [Description("The quantity the order asks for, on an error")] long? RequestedQuantity = null,
    [Description("The quantity in stock, on an error")] long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.GetOrderStatus"/> returns: the status and timestamps of an order, or the error saying that none matches.</summary>
public record OrderStatusResult(
    [Description("Why no status is returned, present only when no order matches")] string? Error = null,
    [Description("The order identifier")] string? OrderId = null,
    [Description("The product SKU")] string? Sku = null,
    [Description("The quantity ordered")] long? Quantity = null,
    [Description("The status of the order")] string? Status = null,
    [Description("When the order was created")] string? CreatedAt = null,
    [Description("When the order was confirmed, absent until then")] string? ConfirmedAt = null,
    [Description("When the order was cancelled, absent until then")] string? CancelledAt = null,
    [Description("The order identifier that was asked for, on an error")] string? RequestedOrderId = null);

/// <summary>What <see cref="InventoryTool.CancelOrder"/> returns: the cancellation just made, or the error saying why it was not.</summary>
public record CancelOrderResult(
    [Description("Why the order was not cancelled")] string? Error = null,
    [Description("The order identifier that was asked for, on an error")] string? RequestedOrderId = null,
    [Description("The order identifier")] string? OrderId = null,
    [Description("The status the order had before the cancellation")] string? PreviousStatus = null,
    [Description("The status of the order")] string? Status = null,
    [Description("When the order was cancelled")] string? CancelledAt = null,
    [Description("Whether the stock of a confirmed order was given back")] bool? StockRestored = null,
    [Description("The cancellation reason that was given")] string? Reason = null);

/// <summary>One order in a listing.</summary>
/// <remarks>The confirmation and cancellation times are written even when null, since a null says that the order never reached that step.</remarks>
public record OrderSummary(
    string OrderId,
    string Sku,
    long Quantity,
    string Status,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConfirmedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CancelledAt);

/// <summary>What <see cref="InventoryTool.GetOrders"/> returns: the orders placed during the current conversation.</summary>
public record OrdersResult(
    [Description("How many orders this conversation placed")] int TotalOrders,
    [Description("The orders with identifier, SKU, quantity, status and timestamps")] List<OrderSummary> Orders);

/// <summary>What <see cref="InventoryTool.GetOrderHistory"/> returns: every order ever placed under a customer code.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record OrderHistoryResult(
    [Description("The customer code the books were asked about")] string CustomerCode,
    [Description("The customer's name when the shop knows the code")] [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    [Description("How many orders the customer ever placed")] int TotalOrders,
    [Description("The orders with identifier, SKU, quantity, status and timestamps")] List<OrderSummary> Orders);
