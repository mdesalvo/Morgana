namespace Morgana.AI.Interfaces;

/// <summary>
/// Finds the native tool class of each intent without hardcoded mappings (plugins included) and projects the tools and workflows that the classes declare.
/// </summary>
/// <remarks>
/// A native domain tool is described by its method on the class: the method's description, its
/// approval attribute, each parameter's description and scope, the returned record's property descriptions.
/// <see cref="GetToolDefinitions"/> projects those into the definitions that the adapter, the composer,
/// the workflow engine and the A2A card read.
/// </remarks>
public interface IToolRegistryService
{
    /// <summary>
    /// Finds the MorganaTool type that provides native tools for the specified intent.
    /// </summary>
    /// <param name="intent">The intent to find a tool for (e.g., "billing", "contract")</param>
    /// <returns>
    /// Type of the MorganaTool class decorated with [ProvidesToolForIntent(intent)];
    /// null if no tool implementation found for this intent.
    /// </returns>
    Type? FindToolTypeForIntent(string intent);

    /// <summary>
    /// Gets all registered tool types with their associated intents.
    /// Returns a snapshot of the tool registry for diagnostics and validation.
    /// </summary>
    /// <returns>
    /// Read-only dictionary mapping intent names to their corresponding MorganaTool types.
    /// </returns>
    IReadOnlyDictionary<string, Type> GetAllRegisteredTools();

    /// <summary>
    /// Gets the tool definitions that the intent's tool class declares, in declaration order.
    /// </summary>
    /// <param name="intent">The intent whose domain tools are wanted (case-insensitive).</param>
    /// <returns>
    /// The definitions projected from the class, never including a framework tool; empty when the
    /// intent has no tool type.
    /// </returns>
    IReadOnlyList<Records.ToolDefinition> GetToolDefinitions(string intent);

    /// <summary>
    /// Gets the workflows that the intent's agent runs, as its <c>MorganaWorkflow</c> classes declare them.
    /// </summary>
    /// <param name="intent">The intent whose workflows are wanted (case-insensitive).</param>
    /// <returns>One definition per workflow class; empty when the intent has none.</returns>
    IReadOnlyList<Records.WorkflowDefinition> GetWorkflowDefinitions(string intent);

    /// <summary>
    /// Gets every workflow that discovery projected, keyed by the intent that its class names, whether or
    /// not an agent handles that intent.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Records.WorkflowDefinition>> GetAllRegisteredWorkflows();
}
