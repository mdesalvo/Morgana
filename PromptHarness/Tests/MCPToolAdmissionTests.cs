using Morgana.AI.Adapters;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting which names an MCP tool may take when it joins an agent: a name already held by
/// Morgana's tools, the domain's or the colleagues' is refused in any case and a free name is admitted.
/// </summary>
/// <remarks>
/// <para>Deterministic and free: the rule is the adapter's own and no MCP server is reached.</para>
///
/// <para>Every literal is spelled out here rather than read from <c>Constants</c>, as the wire-contract
/// groups do: the point is to notice the rule changing shape, not a constant equal to itself.</para>
/// </remarks>
public sealed class MCPToolAdmissionTests
{
    /// <summary>The names of an agent's tools from morgana.json and agents.json.</summary>
    private static readonly string[] AgentToolNames =
    [
        "GetContextVariable",
        "SetContextVariable",
        "SetTurnEnding",
        "SetQuickReplies",
        "SetRichCard",
        "GetInvoices"
    ];

    [Theory]
    [InlineData("GetContextVariable")]
    [InlineData("SetTurnEnding")]
    [InlineData("SetRichCard")]
    [InlineData("GetInvoices")]
    [InlineData("getcontextvariable")]
    [InlineData("GETINVOICES")]
    public void An_mcp_tool_taking_a_name_of_the_agent_is_refused(string mcpToolName)
    {
        // No MCP tool is admitted yet so only the agent's own names can collide.
        string? conflictReason = MorganaAgentAdapter.FindMCPToolNameConflict(mcpToolName, AgentToolNames, []);

        // The name is a base tool's or a domain tool's in some case so the reason must exist.
        Assert.NotNull(conflictReason);
    }

    [Theory]
    [InlineData("consult_billing")]
    [InlineData("consult_acme_shipping")]
    [InlineData("Consult_anything")]
    public void An_mcp_tool_taking_the_prefix_of_the_colleagues_is_refused(string mcpToolName)
    {
        // No MCP tool is admitted yet so only the reserved prefix can collide.
        string? conflictReason = MorganaAgentAdapter.FindMCPToolNameConflict(mcpToolName, AgentToolNames, []);

        // The name opens with the colleagues' prefix in some case so the reason must exist.
        Assert.NotNull(conflictReason);
    }

    [Theory]
    [InlineData("search_bananas")]
    [InlineData("Search_Bananas")]
    public void An_mcp_tool_taking_a_name_another_mcp_source_gave_is_refused(string mcpToolName)
    {
        // The agent already holds an MCP tool admitted from an earlier source.
        string[] admittedMCPToolNames = ["search_bananas"];

        // The name is the admitted tool's in some case so the second source cannot take it.
        string? conflictReason = MorganaAgentAdapter.FindMCPToolNameConflict(mcpToolName, AgentToolNames, admittedMCPToolNames);

        // The reason must exist.
        Assert.NotNull(conflictReason);
    }

    [Theory]
    [InlineData("search_bananas")]
    [InlineData("get_monkey")]
    [InlineData("GetInvoiceHistory")]
    [InlineData("consultation_notes")]
    public void An_mcp_tool_with_a_free_name_is_admitted(string mcpToolName)
    {
        // Nothing is admitted yet so only the agent's names and the prefix could collide.
        string? conflictReason = MorganaAgentAdapter.FindMCPToolNameConflict(mcpToolName, AgentToolNames, []);

        // A name that only begins like another one collides with nothing so no reason is given.
        Assert.Null(conflictReason);
    }
}
