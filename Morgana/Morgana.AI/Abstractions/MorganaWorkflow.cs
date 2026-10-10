using System.ComponentModel;
using System.Reflection;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base of a workflow that a domain author writes as a class: the steps are static fields, the public
/// properties are the values that edges carry and the constructor wires one <c>AddEdge</c> line per transition.
/// </summary>
/// <remarks>
/// A step with no edge for a tool's outcome ends the workflow there.
/// </remarks>
public abstract class MorganaWorkflow
{
    /// <summary>The step where the workflow starts.</summary>
    private readonly Records.WorkflowStep start;

    /// <summary>The steps that edges name, in order of first appearance and distinct by instance.</summary>
    private readonly List<Records.WorkflowStep> steps = [];

    /// <summary>The transitions in the order the constructor declared them.</summary>
    private readonly List<Records.WorkflowEdge> edges = [];

    /// <summary>
    /// Starts the workflow at the given step.
    /// </summary>
    /// <param name="start">The first step; the workflow stands here when it is launched.</param>
    protected MorganaWorkflow(Records.WorkflowStep start)
        => this.start = start;

    /// <summary>
    /// Leads a successful call of the tool at the source step to the target step.
    /// </summary>
    /// <param name="source">The step the call is made at.</param>
    /// <param name="target">The step the workflow moves to.</param>
    /// <param name="tool">The tool whose successful call follows this edge.</param>
    /// <param name="carrying">The properties of the class that the target step takes by name from the call's result.</param>
    protected void AddEdge(Records.WorkflowStep source, Records.WorkflowStep target, string tool, IReadOnlyList<string>? carrying = null)
        => Add(source, target, tool, false, carrying);

    /// <summary>
    /// Leads a failed call of the tool at the source step to the target step.
    /// </summary>
    /// <param name="source">The step the call is made at.</param>
    /// <param name="target">The step the workflow moves to.</param>
    /// <param name="tool">The tool whose failed call follows this edge.</param>
    /// <param name="carrying">The properties of the class that the target step takes by name from the call's result.</param>
    protected void AddFailureEdge(Records.WorkflowStep source, Records.WorkflowStep target, string tool, IReadOnlyList<string>? carrying = null)
        => Add(source, target, tool, true, carrying);

    /// <summary>
    /// Projects the class into the definition that startup validation checks and the engine runs.
    /// </summary>
    public Records.WorkflowDefinition ToDefinition()
    {
        // The domain author's class, which carries the name, the description and the carried values.
        Type workflowClass = GetType();

        // The class name without its suffix is the workflow's name, which is also what the launcher `Start{Name}` carries.
        string name = workflowClass.Name;
        if (name.EndsWith(Constants.Workflows.ClassNameSuffix, StringComparison.Ordinal) && name.Length > Constants.Workflows.ClassNameSuffix.Length)
            name = name[..^Constants.Workflows.ClassNameSuffix.Length];

        // The class description is what the launcher tells the model about the workflow.
        string description = workflowClass.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        // Only the class's own properties are carried values: inherited members are the framework's.
        List<string> parameters = [.. workflowClass
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)];

        // Distinct by instance: two steps sharing a name stay two, so validation can refuse the clash.
        List<Records.WorkflowStep> ordered = [start, .. steps.Where(step => !ReferenceEquals(step, start))];

        // The definition is a snapshot: startup validation and the engine read it and never the class again.
        return new Records.WorkflowDefinition(name, description, ordered, [.. edges], parameters);
    }

    /// <summary>Records one edge and the steps it names.</summary>
    private void Add(Records.WorkflowStep source, Records.WorkflowStep target, string tool, bool onFailure, IReadOnlyList<string>? carrying)
    {
        // Steps exist only through the edges that name them; a step named by several edges is registered once.
        foreach (Records.WorkflowStep step in new[] { source, target })
        {
            if (!steps.Any(known => ReferenceEquals(known, step)))
                steps.Add(step);
        }

        // The edge keeps names only: the graph is rebuilt from them at every call.
        edges.Add(new Records.WorkflowEdge(source.Name, target.Name, tool, onFailure, carrying ?? []));
    }
}