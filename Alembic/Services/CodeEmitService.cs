using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Writes the generated C# of an agent from its Draft with string templates, so the same Draft always emits the same bytes.
/// </summary>
/// <remarks>The shape follows the agent and tool classes of the shipped Examples plugin, so a framework change shows up as a change in this file.</remarks>
public class CodeEmitService : ICodeEmitService
{
    /// <summary>The namespace that the generated classes take when the Draft carries none.</summary>
    public const string DefaultNamespace = "Domain";

    /// <summary>The output ceiling of every Alembic model call, sized for whole source files instead of the tier's ceiling.</summary>
    public const int OutputCeiling = 16384;

    /// <summary>The tier that an agent runs on when the Draft carries none: the middle one, so no budget is spent on a decision nobody made.</summary>
    public const Records.LLMTier DefaultTier = Records.LLMTier.Efficiency;

    /// <summary>The type of every emitted parameter: the Draft records no parameter type, so the client narrows it in the C# if needed.</summary>
    private const string ParameterType = "string";

    /// <inheritdoc />
    public IReadOnlyList<EmittedFile> Emit(AgentDraft agent, string intentName)
    {
        // The classes land in the namespace the client declared or in the default one.
        string ns = string.IsNullOrWhiteSpace(agent.Code.Namespace) ? DefaultNamespace : agent.Code.Namespace!;
        // The agent class keeps the client's name for it or takes one from the intent.
        string agentClass = agent.Code.AgentClassName ?? $"{Pascal(intentName)}Agent";

        // The agent class is always written: it is the one file that every agent needs.
        List<EmittedFile> files =
        [
            new EmittedFile($"Agents/{agentClass}.g.cs", EmitAgent(agent, intentName, ns, agentClass), FileOwnership.Generated)
        ];

        // The tool class keeps the client's name for it or takes one from the intent.
        string toolClass = agent.Code.ToolClassName ?? $"{Pascal(intentName)}Tool";

        // A workflow can stand without a native tool: an MCP-only agent's steps name its servers' tools by literal.
        // A workflow with no name or no step has no class to write yet and validation reports it.
        files.AddRange(agent.Workflows
            .Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name) && workflow.Steps.Count > 0)
            .Select(workflow => new EmittedFile($"Workflows/{workflow.Name}Workflow.g.cs", EmitWorkflow(agent, workflow, intentName, ns, toolClass), FileOwnership.Generated)));

        // An MCP-only agent receives its tools from its servers at runtime, so no tool class is written for it.
        if (agent.Tools.Count == 0)
            return files;

        // The tool signatures are the half of the tool class that Alembic owns.
        files.Add(new EmittedFile($"Tools/{toolClass}.g.cs", EmitToolSignatures(agent, intentName, ns, toolClass), FileOwnership.Generated));

        // The archive receives the agent class together with its workflows and tools.
        return files;
    }

    /// <summary>
    /// Writes the agent class whole: every decision it expresses is already in the Draft, so the client owns no half of it.
    /// </summary>
    private static string EmitAgent(AgentDraft agent, string intentName, string ns, string agentClass)
    {
        StringBuilder sb = new StringBuilder();

        // The file opens with the banner and the usings that the agent base class needs.
        sb.AppendLine(AgentBanner);
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Adapters;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using Morgana.AI.Interfaces;");
        sb.AppendLine("using static Morgana.AI.Records;");
        sb.AppendLine();

        // The attributes bind the class to its intent and to the tier it runs on.
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Agents;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[HandlesIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"[RequiresLLMTier(LLMTier.{agent.Code.Tier ?? DefaultTier})]");

        // Each MCP server the agent uses is declared on the class.
        foreach (string server in agent.Code.MCPServers)
            sb.AppendLine(CultureInfo.InvariantCulture, $"[UsesMCPServer(\"{server}\")]");

        // Each colleague becomes a consult function in the agent's tools, which startup checks against the finished domain.
        // A colleague at another instance is checked against that instance's declaration and its own card.
        foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            sb.AppendLine(colleague.Instance is null
                ? $"[ConsultsAgent(\"{colleague.Intent}\")]"
                : $"[ConsultsAgent(\"{colleague.Intent}\", \"{colleague.Instance}\")]");

        // The class and its constructor hand the agent to the framework's adapter, which builds the model-facing agent.
        sb.AppendLine(CultureInfo.InvariantCulture, $"public partial class {agentClass} : MorganaAgent");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {agentClass}(");
        sb.AppendLine("        string conversationId,");
        sb.AppendLine("        ILLMService llmService,");
        sb.AppendLine("        IPromptResolverService promptResolverService,");
        sb.AppendLine("        IConversationPersistenceService conversationPersistenceService,");
        sb.AppendLine("        ILogger logger,");
        sb.AppendLine("        MorganaAgentAdapter morganaAgentAdapter,");
        sb.AppendLine("        IConfiguration configuration)");
        sb.AppendLine("        : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, configuration)");
        sb.AppendLine("    {");
        sb.AppendLine("        (aiAgent, aiContextProvider, aiChatHistoryProvider)");
        sb.AppendLine("            = morganaAgentAdapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // The agent class file is complete.
        return sb.ToString();
    }

    /// <summary>
    /// Writes the half of the tool class that Alembic owns: one attributed <c>partial</c> signature per tool and its result record.
    /// </summary>
    /// <remarks>A signature left unimplemented in the client's half does not compile, so a tool added to the Draft cannot be forgotten.</remarks>
    private static string EmitToolSignatures(AgentDraft agent, string intentName, string ns, string toolClass)
    {
        StringBuilder sb = new StringBuilder();

        // The banner tells the client which half of the tool class is theirs.
        sb.AppendLine(ToolBanner);

        // A generated file defaults to nullable-disabled, which would warn on every optional parameter.
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using static Morgana.AI.Records;");
        sb.AppendLine();

        // The class is bound to its intent and takes the context accessor that the tool base needs.
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Tools;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[ProvidesToolForIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"public partial class {toolClass} : MorganaTool");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {toolClass}(ILogger toolLogger, Func<ToolContext> getToolContext)");
        sb.AppendLine("        : base(toolLogger, getToolContext) { }");

        // A nameless tool is a validation error that the client has yet to fix: the emit skips it and never blocks.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            // The description and the approval are what the framework reads off the method.
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"    [Description({Literal(tool.Description)})]");

            // Every tool is the user's or the system's: a tool on the agent's menu carries its button, any other is the system's.
            if (tool.MenuEntry is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"    [UserTool({tool.MenuEntry.Ordinal}, {Literal(tool.MenuEntry.Label)})]");
            else
                sb.AppendLine("    [SystemTool]");

            // Only a tool that waits for the user's approval declares it: the framework reads its absence as no approval.
            if (tool.RequiresExecutionApproval)
                sb.AppendLine("    [RequiresApproval]");

            // The signature is followed by the record that the tool returns.
            AppendMethod(sb, tool);

            sb.AppendLine();
            AppendResultRecord(sb, tool);
        }

        // The class holds every tool signature and closes after the last one.
        sb.AppendLine("}");

        // The tool signatures file is complete.
        return sb.ToString();
    }

    /// <summary>
    /// Writes one workflow as a <c>MorganaWorkflow</c> class, whole: the client owns no half of it.
    /// </summary>
    /// <remarks>A tool of the agent's toolkit is written with <c>nameof</c> so a rename breaks the build; any other name is an MCP tool written as a literal.</remarks>
    private static string EmitWorkflow(AgentDraft agent, WorkflowDraft workflow, string intentName, string ns, string toolClass)
    {
        StringBuilder sb = new StringBuilder();

        // The names of the agent's own tools, the ones that nameof can reference.
        HashSet<string> toolkit = [.. agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => t.Name!)];

        // The values that edges carry from one step to the next become properties of the workflow.
        List<string> properties = [.. DraftProjection.CarriedNames(workflow).Select(PropertyName).Distinct(StringComparer.Ordinal)];

        // A native tool is referenced by name so a rename breaks the build; an MCP tool is known to its server alone.
        string ToolReference(string tool) => toolkit.Contains(tool) ? $"nameof({toolClass}.{tool})" : Literal(tool);

        // The file opens with the banner and the usings of the workflow base class.
        sb.AppendLine(AgentBanner);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.ComponentModel;");

        // The tools' namespace exists only where a tool class does.
        if (toolkit.Count > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"using {ns}.Tools;");

        // The framework namespaces carry the attributes and abstractions that the generated declarations use.
        sb.AppendLine("using Morgana.AI;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine();

        // The class is bound to its intent and carries the description that the model reads.
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Workflows;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[ProvidesWorkflowForIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"[Description({Literal(workflow.Description)})]");
        // Every workflow is the user's or the system's: one on the agent's menu carries its button on the class, any other is the system's.
        if (workflow.MenuEntry is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"[UserTool({workflow.MenuEntry.Ordinal}, {Literal(workflow.MenuEntry.Label)})]");
        else
            sb.AppendLine("[SystemTool]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {workflow.Name}Workflow : MorganaWorkflow");
        sb.AppendLine("{");

        // One property per carried value, empty until an edge fills it.
        foreach (string property in properties)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public string? {property} {{ get; init; }}");

        // A blank line separates the properties from the steps.
        if (properties.Count > 0)
            sb.AppendLine();

        // Each step is a static value naming the tools that the model may call there.
        foreach (Records.WorkflowStep step in workflow.Steps)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    private static readonly Records.WorkflowStep {step.Name} = new(\"{step.Name}\", [{string.Join(", ", step.Tools.Select(ToolReference))}]);");

        sb.AppendLine();

        // The constructor starts the workflow at its first step and declares the edges.
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {workflow.Name}Workflow() : base(start: {workflow.Steps[0].Name})");
        sb.AppendLine("    {");

        // Each edge says which step follows a tool's outcome and which values it carries along.
        foreach (Records.WorkflowEdge edge in workflow.Edges)
        {
            // An edge that carries nothing leaves its carrying argument out.
            string cargoArgument = edge.Carrying is { Count: > 0 }
                ? $", carrying: [{string.Join(", ", edge.Carrying.Select(name => $"nameof({PropertyName(name)})"))}]"
                : string.Empty;

            // A failure edge is taken when the tool reports an error and a plain edge when it succeeds.
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        {(edge.OnFailure ? "AddFailureEdge" : "AddEdge")}({edge.Source}, {edge.Target}, {ToolReference(edge.Tool)}{cargoArgument});");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        // The workflow file is complete.
        return sb.ToString();
    }

    /// <summary>
    /// Writes one tool's <c>partial</c> method declaration with every parameter on its own line.
    /// </summary>
    /// <remarks>Parameters keep the Draft's order since it is the signature; a required one behind an optional one is a validator error that gates the emit.</remarks>
    private static void AppendMethod(StringBuilder sb, ToolDraft tool)
    {
        // A parameter with no name is a validation error that the client has yet to fix, so it is left out.
        List<ToolParameterDraft> parameters = [.. tool.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name))];

        // A tool without parameters is a one-line signature.
        if (parameters.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<{ResultTypeName(tool.Name!)}> {tool.Name}();");
            return;
        }

        // The tool name is validated before the emit, so the tool always has one here.
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<{ResultTypeName(tool.Name!)}> {tool.Name}(");

        // The last parameter closes the signature.
        for (int position = 0; position < parameters.Count; position++)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        {Parameter(parameters[position])}{(position < parameters.Count - 1 ? "," : ");")}");
    }

    /// <summary>
    /// Renders one parameter with its description and its scope, the two attributes the framework reads.
    /// </summary>
    private static string Parameter(ToolParameterDraft parameter)
    {
        StringBuilder declaration = new StringBuilder();

        // The description is what the model reads about the parameter.
        declaration.Append(CultureInfo.InvariantCulture, $"[Description({Literal(parameter.Description)})] ");

        // A scope not settled yet is left off rather than guessed: the validator reports it before the archive is built.
        // A context parameter is filled by the system from what it knows of the user and may be shared with other agents.
        if (string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            declaration.Append(parameter.Shared ? "[ToolParameter(ToolScope.Context, shared: true)] " : "[ToolParameter(ToolScope.Context)] ");
        // A request parameter is asked of the user.
        else if (string.Equals(parameter.Scope, Constants.Scopes.Request, StringComparison.OrdinalIgnoreCase))
            declaration.Append("[ToolParameter(ToolScope.Request)] ");

        // An optional parameter defaults to null so a caller may omit it.
        declaration.Append(parameter.Required
            ? $"{ParameterType} {parameter.Name}"
            : $"{ParameterType}? {parameter.Name} = null");

        return declaration.ToString();
    }

    /// <summary>
    /// Writes the record a tool returns, one positional property per declared field.
    /// </summary>
    /// <remarks>A tool with a failure field answers only what it knows on each branch, so its properties are all nullable.</remarks>
    private static void AppendResultRecord(StringBuilder sb, ToolDraft tool)
    {
        // A field with no name is a validation error that the client has yet to fix, so it is left out.
        List<ToolReturnDraft> fields = [.. tool.Returns.Where(r => !string.IsNullOrWhiteSpace(r.Name))];
        // The failure field is what a workflow reads to tell a failed call from a successful one.
        bool hasFailureField = fields.Any(r => r.Name == Constants.Workflows.FailureField);

        // A tool that returns no field gets an empty record.
        if (fields.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public sealed record {ResultTypeName(tool.Name!)}();");
            return;
        }

        // The record is named after the tool and the validated tool name is always present.
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public sealed record {ResultTypeName(tool.Name!)}(");

        // The last field closes the record.
        for (int position = 0; position < fields.Count; position++)
        {
            // The field written at this position of the record.
            ToolReturnDraft field = fields[position];

            // A type not asked yet is written as text so the file still parses: the validator refuses it before an archive is built.
            string type = string.IsNullOrWhiteSpace(field.Type) ? ParameterType : field.Type.Trim();
            // The field description is what the model reads about the returned value.
            string description = string.IsNullOrWhiteSpace(field.Description) ? string.Empty : $"[Description({Literal(field.Description)})] ";
            // With a failure field every property defaults to null; without one it is exactly as typed.
            string property = hasFailureField ? $"{Nullable(type)} {PropertyName(field.Name!)} = null" : $"{type} {PropertyName(field.Name!)}";

            // The field is written with its description and its type.
            sb.AppendLine(CultureInfo.InvariantCulture, $"        {description}{property}{(position < fields.Count - 1 ? "," : ");")}");
        }
    }

    /// <summary>Makes the type of a returned field nullable when it is not already.</summary>
    private static string Nullable(string type) => type.EndsWith('?') ? type : type + "?";

    /// <summary>
    /// Writes a text as a regular C# string literal, escaped so that what is read back is the text as authored.
    /// </summary>
    private static string Literal(string? text)
    {
        StringBuilder literal = new StringBuilder("\"");

        // Any character without an escape below is written as it is.
        foreach (char character in text ?? string.Empty)
            literal.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });

        // The closing quote ends the literal that the emitted file carries.
        return literal.Append('"').ToString();
    }

    /// <summary>
    /// The names in a returned field's type that the client's half must declare: neither built in nor from the common library set.
    /// </summary>
    /// <param name="type">The C# type text of a returned field, such as <c>List&lt;CatalogProduct&gt;?</c>.</param>
    public static IReadOnlyList<string> ClientTypeNames(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? []
            : [.. TypeIdentifier.Matches(type).Select(match => match.Value).Where(name => !LibraryTypeNames.Contains(name)).Distinct(StringComparer.Ordinal)];

    /// <summary>Finds each identifier in a type's text that is not the qualifier of a longer name.</summary>
    private static readonly Regex TypeIdentifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b(?!\s*\.)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The keywords and library types that a returned field commonly names, none of which the client declares.</summary>
    private static readonly HashSet<string> LibraryTypeNames = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string",
        "List", "IList", "ICollection", "IEnumerable", "IReadOnlyList", "IReadOnlyCollection", "Dictionary", "IDictionary", "IReadOnlyDictionary",
        "HashSet", "ISet", "KeyValuePair", "Tuple", "ValueTuple", "Array", "Nullable",
        "DateOnly", "TimeOnly", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "JsonElement", "JsonNode"
    };

    /// <summary>The name of the record that a tool's method returns, which the emit, the mock and the migration report all use.</summary>
    public static string ResultTypeName(string toolName) => $"{toolName}Result";

    /// <summary>The record property that carries a returned field, which the model reads back camelCased.</summary>
    public static string PropertyName(string field) => char.ToUpperInvariant(field[0]) + field[1..];

    /// <summary>Capitalizes an intent name for the class names that the client did not set.</summary>
    private static string Pascal(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Domain" : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>The header of an agent class or a workflow, which has no client half to point at.</summary>
    private const string AgentBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "// </auto-generated>";

    /// <summary>The header of the tool signatures, which points the client at the half that they own.</summary>
    private const string ToolBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "//     The half you own is the matching file without the .g — that one is written once and never touched again.\n" +
        "// </auto-generated>";
}
