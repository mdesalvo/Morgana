using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="ICodeEmitService"/>: string templates and nothing else.
/// </summary>
/// <remarks>
/// No Roslyn, no syntax factory. What is emitted is the shape of `BillingAgent.cs` and
/// `BillingTool.cs` in the shipped Examples plugin, which a template reproduces exactly and a code
/// model would only reproduce approximately. The plugin surface is small, fixed by base classes and
/// attributes and changes when the framework changes — at which point this file changes with it,
/// visibly, rather than a generator quietly emitting something subtly different.
/// </remarks>
public class CodeEmitService : ICodeEmitService
{
    /// <summary>
    /// The namespace used when the Draft carries none.
    /// </summary>
    public const string DefaultNamespace = "Domain";

    /// <summary>
    /// The tier assumed when the Draft carries none.
    /// </summary>
    /// <remarks>
    /// <c>Efficiency</c> is the middle tier. The author picks <c>Economy</c> or <c>Performance</c>
    /// deliberately, so a default must not quietly spend a client's budget on a decision nobody made.
    /// </remarks>
    public const Records.LLMTier DefaultTier = Records.LLMTier.Efficiency;

    /// <summary>
    /// Every emitted C# parameter is a <c>string</c>.
    /// </summary>
    /// <remarks>
    /// Not a shortcut — a statement about what the draft carries. A parameter has a name, a
    /// description, a required flag, a scope and a shared flag and no type: the JSON schema the model
    /// reads is generated from the method, so the type lives in the C# and only there. Alembic cannot
    /// know it and guessing one from a parameter's name would be a guess the client discovers at
    /// runtime. A narrower type is a one-word edit in the two halves; a wrong one is a bug.
    /// </remarks>
    private const string ParameterType = "string";

    /// <inheritdoc />
    public IReadOnlyList<EmittedFile> Emit(AgentDraft agent, string intentName)
    {
        string ns = string.IsNullOrWhiteSpace(agent.Code.Namespace) ? DefaultNamespace : agent.Code.Namespace!;
        string agentClass = agent.Code.AgentClassName ?? $"{Pascal(intentName)}Agent";

        List<EmittedFile> files =
        [
            new EmittedFile($"Agents/{agentClass}.g.cs", EmitAgent(agent, intentName, ns, agentClass), FileOwnership.Generated)
        ];

        string toolClass = agent.Code.ToolClassName ?? $"{Pascal(intentName)}Tool";

        // A workflow can stand without a native tool: an MCP-only agent's steps name its servers' tools by literal.
        // A workflow with no name or no step has no class to write yet and validation reports it.
        files.AddRange(agent.Workflows
            .Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name) && workflow.Steps.Count > 0)
            .Select(workflow => new EmittedFile($"Workflows/{workflow.Name}Workflow.g.cs", EmitWorkflow(agent, workflow, intentName, ns, toolClass), FileOwnership.Generated)));

        // No tool class for an agent that declares none: an MCP-only agent's tools arrive at runtime
        // from its servers and an empty MorganaTool subclass would only invite someone to fill it.
        if (agent.Tools.Count == 0)
            return files;

        files.Add(new EmittedFile($"Tools/{toolClass}.g.cs", EmitToolSignatures(agent, intentName, ns, toolClass), FileOwnership.Generated));

        return files;
    }

    /// <summary>
    /// The agent class, whole. There is no half of it for the client to own.
    /// </summary>
    /// <remarks>
    /// A <c>MorganaAgent</c> subclass is attributes plus one constructor that hands the type to
    /// <c>MorganaAgentAdapter</c>. Every domain decision it expresses — the intent, the tier, the
    /// MCP servers — is configuration Alembic already holds, so there is nothing left to write by
    /// hand. It is still <c>partial</c>, which costs nothing and leaves the door open for a client
    /// who wants to add something of their own beside it.
    /// </remarks>
    private static string EmitAgent(AgentDraft agent, string intentName, string ns, string agentClass)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(AgentBanner);
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Adapters;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using Morgana.AI.Interfaces;");
        sb.AppendLine("using static Morgana.AI.Records;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Agents;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[HandlesIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"[RequiresLLMTier(LLMTier.{agent.Code.Tier ?? DefaultTier})]");

        foreach (string server in agent.Code.MCPServers)
            sb.AppendLine(CultureInfo.InvariantCulture, $"[UsesMCPServer(\"{server}\")]");

        // One per colleague and each one becomes a consult_{intent} function in this agent's tool
        // list at assembly time. Startup refuses an intent no agent handles and refuses the agent's
        // own, which is why the interview settles them against the finished domain. A colleague at an
        // instance is checked differently — startup verifies the instance is declared, addressable and
        // signable and leaves the intent to that instance's own card.

        foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            sb.AppendLine(colleague.Instance is null
                ? $"[ConsultsAgent(\"{colleague.Intent}\")]"
                : $"[ConsultsAgent(\"{colleague.Intent}\", \"{colleague.Instance}\")]");

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

        return sb.ToString();
    }

    /// <summary>
    /// The tool class's half that Alembic owns: the attribute, the constructor, one attributed
    /// <c>partial</c> signature per declared tool and the record that each one returns.
    /// </summary>
    /// <remarks>
    /// The attributes are the whole declaration of the tool: the framework reads the description, the
    /// approval, each parameter's scope and each returned field off this class and refuses the
    /// plugin at startup where one is missing. A <c>partial</c> method declared here and unimplemented
    /// in the other half does not compile, so a tool added to the draft cannot be silently forgotten
    /// in the code.
    /// </remarks>
    private static string EmitToolSignatures(AgentDraft agent, string intentName, string ns, string toolClass)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(ToolBanner);

        // Roslyn treats a .g.cs file carrying an <auto-generated> banner as generated code and
        // defaults its nullable-annotation context to disabled regardless of the project's own
        // <Nullable>enable</Nullable> — so the `string?` on an optional parameter below would warn
        // CS8669 without this line making the opt-in explicit, file by file, the same way the emitted
        // .csproj already declares it project-wide.
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using static Morgana.AI.Records;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Tools;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[ProvidesToolForIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"public partial class {toolClass} : MorganaTool");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {toolClass}(ILogger toolLogger, Func<ToolContext> getToolContext)");
        sb.AppendLine("        : base(toolLogger, getToolContext) { }");

        // A tool with no name failed ValidateTool as an error, so this loop's filter only ever
        // skips a Draft the client has not yet fixed — the emit itself never blocks on it.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"    [Description({Literal(tool.Description)})]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    [RequiresApproval({(tool.RequiresExecutionApproval ? "true" : "false")})]");
            AppendMethod(sb, tool);

            sb.AppendLine();
            AppendResultRecord(sb, tool);
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// One workflow as a <c>MorganaWorkflow</c> class, whole: there is no half of it for the client to own.
    /// </summary>
    /// <remarks>
    /// A tool of the agent's toolkit is written as <c>nameof</c> so that a tool renamed in the draft
    /// breaks the build instead of the workflow at run time. Any other name is an MCP tool that only
    /// the server knows and is written as a literal.
    /// </remarks>
    private static string EmitWorkflow(AgentDraft agent, WorkflowDraft workflow, string intentName, string ns, string toolClass)
    {
        StringBuilder sb = new StringBuilder();
        HashSet<string> toolkit = [.. agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => t.Name!)];
        List<string> properties = [.. DraftProjection.CarriedNames(workflow).Select(PropertyName).Distinct(StringComparer.Ordinal)];

        string ToolReference(string tool) => toolkit.Contains(tool) ? $"nameof({toolClass}.{tool})" : Literal(tool);

        sb.AppendLine(AgentBanner);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.ComponentModel;");

        // The tools' namespace exists only where a tool class does.
        if (toolkit.Count > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"using {ns}.Tools;");

        sb.AppendLine("using Morgana.AI;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns}.Workflows;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[ProvidesWorkflowForIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"[Description({Literal(workflow.Description)})]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {workflow.Name}Workflow : MorganaWorkflow");
        sb.AppendLine("{");

        foreach (string property in properties)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public string? {property} {{ get; init; }}");

        if (properties.Count > 0)
            sb.AppendLine();

        foreach (Records.WorkflowStep step in workflow.Steps)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    private static readonly Records.WorkflowStep {step.Name} = new(\"{step.Name}\", [{string.Join(", ", step.Tools.Select(ToolReference))}]);");

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {workflow.Name}Workflow() : base(start: {workflow.Steps[0].Name})");
        sb.AppendLine("    {");

        foreach (Records.WorkflowEdge edge in workflow.Edges)
        {
            string carrying = edge.Carrying is { Count: > 0 }
                ? $", carrying: [{string.Join(", ", edge.Carrying.Select(name => $"nameof({PropertyName(name)})"))}]"
                : string.Empty;

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        {(edge.OnFailure ? "AddFailureEdge" : "AddEdge")}({edge.Source}, {edge.Target}, {ToolReference(edge.Tool)}{carrying});");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Writes one tool's <c>partial</c> method declaration with every parameter on its own line.
    /// </summary>
    /// <remarks>
    /// Declaration order, never sorted: the order is the signature and silently reordering here would
    /// emit C# that disagrees with the draft. A required parameter behind an optional one therefore
    /// emits C# that does not compile — which is correct and is why the emit is gated on the validator
    /// reporting no errors: that exact case is one of its findings, raised while it still costs nothing to fix.
    /// </remarks>
    private static void AppendMethod(StringBuilder sb, ToolDraft tool)
    {
        List<ToolParameterDraft> parameters = [.. tool.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name))];

        if (parameters.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<{ResultTypeName(tool.Name!)}> {tool.Name}();");
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<{ResultTypeName(tool.Name!)}> {tool.Name}(");

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

        declaration.Append(CultureInfo.InvariantCulture, $"[Description({Literal(parameter.Description)})] ");

        // A scope not settled yet is left off rather than guessed: the validator reports it before the archive is built.
        if (string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            declaration.Append(parameter.Shared ? "[ToolParameter(ToolScope.Context, shared: true)] " : "[ToolParameter(ToolScope.Context)] ");
        else if (string.Equals(parameter.Scope, Constants.Scopes.Request, StringComparison.OrdinalIgnoreCase))
            declaration.Append("[ToolParameter(ToolScope.Request)] ");

        declaration.Append(parameter.Required
            ? $"{ParameterType} {parameter.Name}"
            : $"{ParameterType}? {parameter.Name} = null");

        return declaration.ToString();
    }

    /// <summary>
    /// Writes the record a tool returns, one positional property per declared field.
    /// </summary>
    /// <remarks>
    /// A tool with a failure field answers only what it knows on each branch, so every property is
    /// nullable with a null default and the failure field stays empty wherever the call succeeds.
    /// Without one the record is exactly as typed.
    /// </remarks>
    private static void AppendResultRecord(StringBuilder sb, ToolDraft tool)
    {
        List<ToolReturnDraft> fields = [.. tool.Returns.Where(r => !string.IsNullOrWhiteSpace(r.Name))];
        bool hasFailureField = fields.Any(r => r.Name == Constants.Workflows.FailureField);

        if (fields.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public sealed record {ResultTypeName(tool.Name!)}();");
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    public sealed record {ResultTypeName(tool.Name!)}(");

        for (int position = 0; position < fields.Count; position++)
        {
            ToolReturnDraft field = fields[position];

            // A type not asked yet is written as text so the file still parses: the validator refuses it before an archive is built.
            string type = string.IsNullOrWhiteSpace(field.Type) ? ParameterType : field.Type.Trim();
            string description = string.IsNullOrWhiteSpace(field.Description) ? string.Empty : $"[Description({Literal(field.Description)})] ";
            string property = hasFailureField ? $"{Nullable(type)} {PropertyName(field.Name!)} = null" : $"{type} {PropertyName(field.Name!)}";

            sb.AppendLine(CultureInfo.InvariantCulture, $"        {description}{property}{(position < fields.Count - 1 ? "," : ");")}");
        }
    }

    /// <summary>
    /// Makes a C# type nullable, leaving one that already is.
    /// </summary>
    private static string Nullable(string type) => type.EndsWith('?') ? type : type + "?";

    /// <summary>
    /// Writes a text as a regular C# string literal, escaped so that what is read back is the text as authored.
    /// </summary>
    private static string Literal(string? text)
    {
        StringBuilder literal = new StringBuilder("\"");

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

        return literal.Append('"').ToString();
    }

    /// <summary>
    /// The names a returned field's type refers to that are neither built in nor from the common library set.
    /// </summary>
    /// <remarks>
    /// Such a type is the client's: the generated half declares only the result records, so the half
    /// the client owns has to declare the others.
    /// </remarks>
    /// <param name="type">The C# type text of a returned field, such as <c>List&lt;CatalogProduct&gt;?</c>.</param>
    public static IReadOnlyList<string> ClientTypeNames(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? []
            : [.. TypeIdentifier.Matches(type).Select(match => match.Value).Where(name => !LibraryTypeNames.Contains(name)).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// An identifier inside a type's text that is not the qualifier of a longer name.
    /// </summary>
    private static readonly Regex TypeIdentifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b(?!\s*\.)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The keywords and the library types that a returned field commonly names, none of which a client declares.
    /// </summary>
    private static readonly HashSet<string> LibraryTypeNames = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string",
        "List", "IList", "ICollection", "IEnumerable", "IReadOnlyList", "IReadOnlyCollection", "Dictionary", "IDictionary", "IReadOnlyDictionary",
        "HashSet", "ISet", "KeyValuePair", "Tuple", "ValueTuple", "Array", "Nullable",
        "DateOnly", "TimeOnly", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "JsonElement", "JsonNode"
    };

    /// <summary>
    /// The name of the record a tool's method returns, shared by the emit, the mock and the migration report.
    /// </summary>
    public static string ResultTypeName(string toolName) => $"{toolName}Result";

    /// <summary>
    /// The record property that carries a returned field, named the way the model reads the field back camelCased.
    /// </summary>
    public static string PropertyName(string field) => char.ToUpperInvariant(field[0]) + field[1..];

    /// <summary>
    /// PascalCases an intent name, for the fallback class names.
    /// </summary>
    private static string Pascal(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Domain" : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>
    /// The header an agent class carries.
    /// </summary>
    /// <remarks>
    /// An agent has no client half — see the remarks on <see cref="EmitAgent"/> — so this banner
    /// says only that it is regenerated in full and stops there rather than pointing at a matching
    /// file without the <c>.g</c> that will never exist.
    /// </remarks>
    private const string AgentBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "// </auto-generated>";

    /// <summary>
    /// The header a tool class's generated signatures carry.
    /// </summary>
    private const string ToolBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "//     The half you own is the matching file without the .g — that one is written once and never touched again.\n" +
        "// </auto-generated>";
}
