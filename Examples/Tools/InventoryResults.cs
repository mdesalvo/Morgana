using System.Text.Json.Serialization;

namespace Examples.Tools;

/// <summary>What <see cref="InventoryTool.GetProductCatalog"/> returns: every plant of the catalog with its stock status.</summary>
public record ProductCatalogResult(int TotalProducts, List<CatalogProduct> Products);

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
    string? Error = null,
    string? Sku = null,
    string? Name = null,
    string? Category = null,
    long? QuantityOnHand = null,
    long? ReorderThreshold = null,
    double? UnitPrice = null,
    string? StockStatus = null,
    string? StatusIcon = null,
    long? MaxOrderableQuantity = null,
    string? RequestedSku = null,
    List<string>? AvailableSkus = null);

/// <summary>What <see cref="InventoryTool.CreatePurchaseOrder"/> returns: the quote just created, or the error saying why none was.</summary>
public record PurchaseOrderResult(
    string? Error = null,
    string? OrderId = null,
    string? SealWord = null,
    string? Sku = null,
    string? ProductName = null,
    int? Quantity = null,
    double? UnitPrice = null,
    double? TotalPrice = null,
    string? Status = null,
    string? Note = null,
    string? RequestedSku = null,
    List<string>? AvailableSkus = null,
    int? RequestedQuantity = null,
    long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.ConfirmOrder"/> returns: the order just confirmed, or the error saying why it was not.</summary>
public record ConfirmOrderResult(
    string? Error = null,
    string? RequestedOrderId = null,
    string? OrderId = null,
    string? Sku = null,
    long? Quantity = null,
    string? Status = null,
    string? ConfirmedAt = null,
    long? RemainingStock = null,
    string? InvoiceId = null,
    string? Note = null,
    long? RequestedQuantity = null,
    long? AvailableQuantity = null);

/// <summary>What <see cref="InventoryTool.GetOrderStatus"/> returns: the status and timestamps of an order, or the error saying that none matches.</summary>
public record OrderStatusResult(
    string? Error = null,
    string? OrderId = null,
    string? Sku = null,
    long? Quantity = null,
    string? Status = null,
    string? CreatedAt = null,
    string? ConfirmedAt = null,
    string? CancelledAt = null,
    string? RequestedOrderId = null);

/// <summary>What <see cref="InventoryTool.CancelOrder"/> returns: the cancellation just made, or the error saying why it was not.</summary>
public record CancelOrderResult(
    string? Error = null,
    string? RequestedOrderId = null,
    string? OrderId = null,
    string? PreviousStatus = null,
    string? Status = null,
    string? CancelledAt = null,
    bool? StockRestored = null,
    string? Reason = null);

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
public record OrdersResult(int TotalOrders, List<OrderSummary> Orders);

/// <summary>What <see cref="InventoryTool.GetOrderHistory"/> returns: every order ever placed under a customer code.</summary>
/// <remarks>The customer name is written even when null, since a null says that the shop does not know the code.</remarks>
public record OrderHistoryResult(
    string CustomerCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CustomerName,
    int TotalOrders,
    List<OrderSummary> Orders);
