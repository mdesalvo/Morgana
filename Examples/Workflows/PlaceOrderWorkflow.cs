using System.ComponentModel;
using Examples.Tools;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;

namespace Examples.Workflows;

/// <summary>
/// The order procedure of the greenhouse: a quote is created, then the customer confirms or cancels it.
/// </summary>
[ProvidesWorkflowForIntent("inventory")]
[Description("Placing a new order for a plant, from its quote to its confirmation or cancellation.")]
public sealed class PlaceOrderWorkflow : MorganaWorkflow
{
    public string? OrderId { get; init; }
    public string? SealWord { get; init; }

    private static readonly Records.WorkflowStep Quote = new("Quote", [nameof(InventoryTool.CreatePurchaseOrder)]);
    private static readonly Records.WorkflowStep Decide = new("Decide", [nameof(InventoryTool.ConfirmOrder), nameof(InventoryTool.CancelOrder)]);

    public PlaceOrderWorkflow() : base(start: Quote)
    {
        AddEdge(Quote, Decide, nameof(InventoryTool.CreatePurchaseOrder), carrying: [nameof(OrderId), nameof(SealWord)]);
        AddFailureEdge(Quote, Quote, nameof(InventoryTool.CreatePurchaseOrder));
    }
}
