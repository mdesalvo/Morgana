using System.Globalization;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="ICodeEmitService"/>: string templates and nothing else.
/// What is emitted is the shape of the shipped example plugin's agent and tool classes, which a template reproduces exactly; when the framework's small
/// plugin surface changes, this file changes with it visibly rather than a code model quietly emitting something subtly different.
/// </summary>
public class CodeEmitService : ICodeEmitService
{
    /// <summary>The namespace used when the Draft carries none.</summary>
    public const string DefaultNamespace = "Domain";

    /// <summary>
    /// The tier assumed when the Draft carries none: Efficiency, the cheaper of the two an agent may run on.
    /// The framework reserves Performance for an author declaring an existential need, so a default picking it would spend a budget on a decision nobody made.
    /// </summary>
    public const Records.LLMTier DefaultTier = Records.LLMTier.Efficiency;

    /// <summary>
    /// The type of every emitted parameter, a statement about what the configuration carries rather than a shortcut.
    /// A tool parameter's record has no type, the model's schema coming from the method itself, so a type guessed from a name would be a guess the client meets at runtime.
    /// </summary>
    private const string ParameterType = "string";

    /// <summary>Emits the agent's class and, where it declares native tools, the generated half of its tool class.</summary>
    public IReadOnlyList<EmittedFile> Emit(AgentDraft agent, string intentName)
    {
        // The namespace and class name the interview settled, else the conventions the framework's own examples follow.
        string codeNamespace = string.IsNullOrWhiteSpace(agent.Code.Namespace) ? DefaultNamespace : agent.Code.Namespace!;
        string agentClass = agent.Code.AgentClassName ?? $"{Pascal(intentName)}Agent";

        // The agent class is always emitted, whole.
        List<EmittedFile> files =
        [
            new EmittedFile($"Agents/{agentClass}.g.cs", EmitAgent(agent, intentName, codeNamespace, agentClass), FileOwnership.Generated)
        ];

        // No tool class for an agent that declares none: an MCP-only agent's tools arrive at runtime
        // from its servers and an empty MorganaTool subclass would only invite someone to fill it.
        if (agent.Tools.Count == 0)
            return files;

        // The tool class's name, which the client's own half must share.
        string toolClass = agent.Code.ToolClassName ?? $"{Pascal(intentName)}Tool";

        // The declared half only: the client's half is a mock the packager asks a model for.
        files.Add(new EmittedFile($"Tools/{toolClass}.g.cs", EmitToolSignatures(agent, intentName, codeNamespace, toolClass), FileOwnership.Generated));

        return files;
    }

    /// <summary>
    /// The agent class, whole, since every decision it expresses, the intent, the tier and the MCP servers, is configuration Alembic already holds.
    /// It is still partial, which costs nothing and leaves room for a client who wants to add something of their own beside it.
    /// </summary>
    private static string EmitAgent(AgentDraft agent, string intentName, string codeNamespace, string agentClass)
    {
        StringBuilder sb = new StringBuilder();

        // The banner, the usings a plugin agent needs and its namespace.
        sb.AppendLine(AgentBanner);
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Adapters;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using Morgana.AI.Interfaces;");
        sb.AppendLine("using static Morgana.AI.Records;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {codeNamespace}.Agents;");
        sb.AppendLine();

        // The two attributes the startup checks require of every agent: its intent and its tier.
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
        // own, which is why the interview settles them against the finished domain. A colleague at a
        // partner is checked differently: startup verifies the partner is declared, addressable and
        // signable and leaves the intent to that partner's own card.
        foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            sb.AppendLine(colleague.Instance is null
                ? $"[ConsultsAgent(\"{colleague.Intent}\")]"
                : $"[ConsultsAgent(\"{colleague.Intent}\", \"{colleague.Instance}\")]");

        // The one constructor every plugin agent has, handing its dependencies to the base, which builds the agent.
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

        // The agent's whole source file.
        return sb.ToString();
    }

    /// <summary>
    /// The half of the tool class Alembic owns: the attribute, the constructor and one attributed partial declaration per declared tool.
    /// The declaration is the tool's whole contract, its description and each parameter's description and scope sitting on the attributes Morgana reads.
    /// </summary>
    private static string EmitToolSignatures(AgentDraft agent, string intentName, string codeNamespace, string toolClass)
    {
        StringBuilder sb = new StringBuilder();

        // The banner tells whoever opens the file that it is regenerated and where their own half is.
        sb.AppendLine(ToolBanner);

        // Roslyn treats a .g.cs file carrying an <auto-generated> banner as generated code and
        // defaults its nullable-annotation context to disabled regardless of the project's own
        // <Nullable>enable</Nullable> — so the `string?` on an optional parameter below would warn
        // CS8669 without this line making the opt-in explicit, file by file, the same way the emitted
        // .csproj already declares it project-wide.
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        // The usings the attributes and the base class need, then the namespace and the class with its constructor.
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine("using Morgana.AI.Tools;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"namespace {codeNamespace}.Tools;");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[ProvidesToolForIntent(\"{intentName}\")]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"public partial class {toolClass} : MorganaTool");
        sb.AppendLine("{");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    public {toolClass}(ILogger toolLogger, Func<ToolContext> getToolContext)");
        sb.AppendLine("        : base(toolLogger, getToolContext) { }");

        // A tool with no name failed ValidateTool as an error, so this loop's filter only ever
        // skips a Draft the client has not yet fixed — the emit itself never blocks on it.
        foreach (ToolDraft tool in agent.Tools.Where(toolDraft => !string.IsNullOrWhiteSpace(toolDraft.Name)))
        {
            // A blank line between tools, as a hand-written class would have.
            sb.AppendLine();

            // The description is an attribute on the partial method: Morgana reads it from the
            // declaration alone, so the client's half carries nothing but the implementation.
            sb.AppendLine(CultureInfo.InvariantCulture, $"    [Description({Literal(tool.Description)})]");

            // The partial declaration the client's half must implement with the same signature.
            AppendDeclaration(sb, tool);
        }

        sb.AppendLine("}");

        // The generated half's whole source file.
        return sb.ToString();
    }

    /// <summary>
    /// Appends one tool's partial method declaration with one attributed parameter per line, in declaration order.
    /// A required parameter behind an optional one would not compile, which is why the archive is withheld while the validator reports an error.
    /// </summary>
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

    /// <summary>Renders one parameter with its description and scope attributes.</summary>
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

        // The description first, where Morgana's schema reads it for the model.
        return $"[Description({Literal(parameter.Description)})]{scopeAttribute} {declaration}";
    }

    /// <summary>The <c>Records.ToolScope</c> member a parameter maps to; <c>null</c> for a value the model authors.</summary>
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

    /// <summary>Renders text as a C# regular string literal.</summary>
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

    /// <summary>PascalCases an intent name, for the fallback class names.</summary>
    private static string Pascal(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Domain" : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>
    /// The header an agent class carries, saying only that it is regenerated in full.
    /// An agent has no client half, so the banner points at no matching file that would never exist.
    /// </summary>
    private const string AgentBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "// </auto-generated>";

    /// <summary>The header a tool class's generated half carries, pointing at the half the client owns.</summary>
    private const string ToolBanner =
        "// <auto-generated>\n" +
        "//     Written by Alembic. This file is regenerated in full: edit it and the edit is lost.\n" +
        "//     The half you own is the matching file without the .g — that one is written once and never touched again.\n" +
        "// </auto-generated>";
}