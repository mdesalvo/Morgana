using System.Text;
using Distiller.Interfaces;
using Distiller.Model;
using Morgana.AI;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="ICodeEmitService"/>: string templates, and nothing else.
/// </summary>
/// <remarks>
/// No Roslyn, no syntax factory. What is emitted is the shape of `BillingAgent.cs` and
/// `BillingTool.cs` in the shipped Examples plugin, which a template reproduces exactly and a code
/// model would only reproduce approximately. The plugin surface is small, fixed by base classes and
/// attributes, and changes when the framework changes — at which point this file changes with it,
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
    /// <c>Efficiency</c>, and deliberately the cheap one: the framework reserves
    /// <c>Performance</c> for an agent whose author declares an existential need for deep
    /// reasoning, and a default that quietly picks it would spend a client's budget on a decision
    /// nobody made.
    /// </remarks>
    public const Records.LLMTier DefaultTier = Records.LLMTier.Efficiency;

    /// <summary>
    /// Every emitted C# parameter is a <c>string</c>.
    /// </summary>
    /// <remarks>
    /// Not a shortcut — a statement about what the configuration carries.
    /// <c>Records.ToolParameter</c> has a name, a description, a required flag, a scope and a
    /// shared flag, and no type: the JSON schema the model reads is generated from the
    /// <em>delegate</em>, so the type lives in the C# and only there. Alembic cannot know it, and
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
        // from its servers, and an empty MorganaTool subclass would only invite someone to fill it.
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
        sb.AppendLine($"namespace {ns}.Agents;");
        sb.AppendLine();
        sb.AppendLine($"[HandlesIntent(\"{intentName}\")]");
        sb.AppendLine($"[RequiresLLMTier(LLMTier.{agent.Code.Tier ?? DefaultTier})]");

        foreach (string server in agent.Code.MCPServers)
            sb.AppendLine($"[UsesMCPServer(\"{server}\")]");

        // One per colleague, and each one becomes a consult_{intent} function in this agent's tool
        // list at assembly time. Startup refuses an intent no agent handles and refuses the agent's
        // own, which is why the interview settles them against the finished domain. A colleague at a
        // partner is checked differently — startup verifies the partner is declared, addressable and
        // signable, and leaves the intent to that partner's own card.

        foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            sb.AppendLine(colleague.Partner is null
                ? $"[ConsultsAgent(\"{colleague.Intent}\")]"
                : $"[ConsultsAgent(\"{colleague.Intent}\", \"{colleague.Partner}\")]");

        sb.AppendLine($"public partial class {agentClass} : MorganaAgent");
        sb.AppendLine("{");
        sb.AppendLine($"    public {agentClass}(");
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
    /// The tool class's half that Alembic owns: the attribute, the constructor, and one
    /// <c>partial</c> signature per declared tool.
    /// </summary>
    /// <remarks>
    /// The signatures are the load-bearing part. They are generated from the same
    /// <c>ToolDefinition</c> that goes into <c>agents.json</c>, so the pair
    /// <c>MorganaToolAdapter.AddTool</c> validates at startup — parameter count, names,
    /// required-versus-optional — is generated correct by construction. And because a partial method
    /// declared here and unimplemented in the other half does not compile, a tool added to the
    /// configuration cannot be silently forgotten in the code.
    /// </remarks>
    private static string EmitToolSignatures(AgentDraft agent, string intentName, string ns, string toolClass)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(ToolBanner);

        // Roslyn treats a .g.cs file carrying an <auto-generated> banner as generated code, and
        // defaults its nullable-annotation context to disabled regardless of the project's own
        // <Nullable>enable</Nullable> — so the `string?` on an optional parameter below would warn
        // CS8669 without this line making the opt-in explicit, file by file, the same way the emitted
        // .csproj already declares it project-wide.
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Morgana.AI.Abstractions;");
        sb.AppendLine("using Morgana.AI.Attributes;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.Tools;");
        sb.AppendLine();
        sb.AppendLine($"[ProvidesToolForIntent(\"{intentName}\")]");
        sb.AppendLine($"public partial class {toolClass} : MorganaTool");
        sb.AppendLine("{");
        sb.AppendLine($"    public {toolClass}(ILogger toolLogger, Func<ToolContext> getToolContext)");
        sb.AppendLine("        : base(toolLogger, getToolContext) { }");

        // A tool with no name failed ValidateTool as an error, so this loop's filter only ever
        // skips a Draft the client has not yet fixed — the emit itself never blocks on it.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            sb.AppendLine();

            // The description becomes the XML doc the client's IDE shows over the partial method
            // they implement — the only place that description survives past agents.json.
            foreach (string line in Wrap(tool.Description))
                sb.AppendLine($"    /// {line}");

            sb.AppendLine($"    public partial Task<string> {tool.Name}({Signature(tool)});");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Renders one tool's parameter list, in the order the configuration declares it.
    /// </summary>
    /// <remarks>
    /// Declaration order, never sorted. The order in <c>agents.json</c> is the contract, and
    /// silently reordering here would emit C# that disagrees with the file it ships beside. A
    /// required parameter behind an optional one therefore emits C# that does not compile — which is
    /// correct, and is why the emit is gated on the validator reporting no errors: that exact case is
    /// one of its findings, raised while it still costs nothing to fix.
    /// </remarks>
    private static string Signature(ToolDraft tool) =>
        string.Join(", ", tool.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Required
                ? $"{ParameterType} {p.Name}"
                : $"{ParameterType}? {p.Name} = null"));

    /// <summary>
    /// Wraps a description into an XML doc comment, escaped.
    /// </summary>
    /// <remarks>
    /// Escaping is XML-only, not Markdown: a description is authored prose, not C#, and stray
    /// <c>&lt;</c>/<c>&amp;</c> characters in it would otherwise be read as XML doc markup by any
    /// tool that later parses the emitted file.
    /// </remarks>
    private static IEnumerable<string> Wrap(string? description)
    {
        // Line endings become spaces rather than being dropped: a description authored as several
        // sentences on several lines must still read as one continuous <summary>, since XML doc
        // comments have no paragraph break of their own short of a second <para>.
        string text = (description ?? string.Empty)
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .ReplaceLineEndings(" ")
            .Trim();

        yield return "<summary>";

        foreach (string line in Chunk(text, 96))
            yield return line;

        yield return "</summary>";
    }

    /// <summary>
    /// Breaks text on word boundaries at a column width.
    /// </summary>
    private static IEnumerable<string> Chunk(string text, int width)
    {
        StringBuilder line = new StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');

            line.Append(word);
        }

        if (line.Length > 0)
            yield return line.ToString();
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
    /// says only that it is regenerated in full, and stops there rather than pointing at a matching
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
