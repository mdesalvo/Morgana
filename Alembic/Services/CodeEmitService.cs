using System.Globalization;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;
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
    /// <c>Efficiency</c> and deliberately the cheap one: the framework reserves
    /// <c>Performance</c> for an agent whose author declares an existential need for deep
    /// reasoning and a default that quietly picks it would spend a client's budget on a decision
    /// nobody made.
    /// </remarks>
    public const Records.LLMTier DefaultTier = Records.LLMTier.Efficiency;

    /// <summary>
    /// Every emitted C# parameter is a <c>string</c>.
    /// </summary>
    /// <remarks>
    /// Not a shortcut — a statement about what the configuration carries.
    /// <c>Records.ToolParameter</c> has a name, a description, a required flag, a scope and a
    /// shared flag and no type: the JSON schema the model reads is generated from the
    /// <em>delegate</em>, so the type lives in the C# and only there. Alembic cannot know it and
    /// guessing one from a parameter's name would be a guess the client discovers at runtime.
    /// A narrower type is a one-word edit in the two halves; a wrong one is a bug.
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

        // No tool class for an agent that declares none: an MCP-only agent's tools arrive at runtime
        // from its servers and an empty MorganaTool subclass would only invite someone to fill it.
        if (agent.Tools.Count == 0)
            return files;

        string toolClass = agent.Code.ToolClassName ?? $"{Pascal(intentName)}Tool";

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

        // Whoever reads the agent's code reads the door its servers open: Morgana's guard cannot see behind it.
        if (agent.Code.MCPServers.Count > 0)
            sb.AppendLine("// Morgana reads what these servers offer and return, never what they do with what they are sent: each one is trusted with the user's data.");

        // One attribute per declared server, whose admitted tools the agent is handed when it is created.
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
        sb.AppendLine("        ILanguageModelService llmService,");
        sb.AppendLine("        IPromptResolverService promptResolverService,");
        sb.AppendLine("        IConversationPersistenceService conversationPersistenceService,");
        sb.AppendLine("        ILogger logger,");
        sb.AppendLine("        MorganaAgentAdapter morganaAgentAdapter,");
        sb.AppendLine("        IConfiguration configuration)");
        sb.AppendLine("        : base(conversationId, llmService, promptResolverService, conversationPersistenceService, logger, morganaAgentAdapter, configuration) { }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// The tool class's half that Alembic owns: the attribute, the constructor and one attributed
    /// <c>partial</c> declaration per declared tool.
    /// </summary>
    /// <remarks>
    /// The declaration is the tool's whole contract: its description and each parameter's description and scope sit on attributes, which is where Morgana reads them.
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
        sb.AppendLine("using Morgana.AI;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using Morgana.AI.Tools;");
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

            // The description is an attribute on the partial method: Morgana reads it from the
            // declaration alone, so the client's half carries nothing but the implementation.
            sb.AppendLine(CultureInfo.InvariantCulture, $"    [Description({Literal(tool.Description)})]");

            AppendDeclaration(sb, tool);
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Appends one tool's <c>partial</c> method declaration with one attributed parameter per line.
    /// </summary>
    /// <remarks>
    /// Declaration order, never sorted: a required parameter behind an optional one emits C# that does not compile, which is why the emit is gated on the validator reporting no errors.
    /// </remarks>
    private static void AppendDeclaration(StringBuilder sb, ToolDraft tool)
    {
        // A parameter with no name is a Draft the client has not yet fixed and is skipped like a nameless tool.
        List<string> parameters = [.. tool.Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .Select(DeclareParameter)];

        // A tool without parameters keeps the one-line form: there is nothing to put on its own line.
        if (parameters.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<string> {tool.Name}();");
            return;
        }

        // Each parameter carries a description and possibly a scope attribute, so each takes its own line.
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public partial Task<string> {tool.Name}(");
        sb.AppendLine(string.Join("," + Environment.NewLine, parameters.Select(parameter => $"        {parameter}")) + ");");
    }

    /// <summary>
    /// Renders one parameter with its description and scope attributes.
    /// </summary>
    private static string DeclareParameter(ToolParameterDraft parameter)
    {
        // The scope attribute is absent for a value the model authors: the parameter then carries only its description.
        string scopeAttribute = ScopeOf(parameter) is { } scope
            ? $" [ToolParameter(Records.ToolScope.{scope})]"
            : string.Empty;

        // The type stays a string and the optional form carries a default, as the validator expects.
        string declaration = parameter.Required
            ? $"{ParameterType} {parameter.Name}"
            : $"{ParameterType}? {parameter.Name} = null";

        return $"[Description({Literal(parameter.Description)})]{scopeAttribute} {declaration}";
    }

    /// <summary>
    /// The <c>Records.ToolScope</c> member a parameter maps to or <c>null</c> for a value the model authors.
    /// </summary>
    private static string? ScopeOf(ToolParameterDraft parameter)
    {
        // A shared parameter is a context value published to every agent of the conversation.
        if (string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            return parameter.Shared ? "SharedContext" : "Context";

        // A request value is asked of the user on the turn it is needed.
        if (string.Equals(parameter.Scope, Constants.Scopes.Request, StringComparison.OrdinalIgnoreCase))
            return "Request";

        // No scope: the model authors the value itself.
        return null;
    }

    /// <summary>
    /// Renders text as a C# regular string literal.
    /// </summary>
    private static string Literal(string? text)
    {
        // The opening quote starts the literal the closing one completes below.
        StringBuilder literal = new StringBuilder("\"");

        // An authored description may hold quotes and line breaks that would end or break the C# string.
        foreach (char character in text ?? string.Empty)
        {
            // Quote, backslash and the common control characters take their short escape; any other
            // control character takes a four-digit hex escape; everything else is written as it is.
            switch (character)
            {
                case '"':
                    literal.Append("\\\"");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                case char controlCharacter when char.IsControl(controlCharacter):
                    literal.Append(CultureInfo.InvariantCulture, $"\\u{(int)controlCharacter:x4}");
                    break;
                default:
                    literal.Append(character);
                    break;
            }
        }

        // The closing quote completes the literal the opening one started.
        literal.Append('"');

        // The finished literal goes straight into the attribute that carries the description.
        return literal.ToString();
    }

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
