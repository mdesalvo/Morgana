using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Services;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Service for creating SummarizingChatReducer instances based on configuration.
/// Provides intelligent context window management with LLM-based summarization.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service centralizes the creation of IChatReducer instances for Morgana agents.
/// It reads configuration from appsettings.json and creates appropriately configured
/// SummarizingChatReducer instances that automatically manage conversation history length.</para>
/// <para><strong>Summarization Strategy:</strong></para>
/// <para>Uses Microsoft's SummarizingChatReducer which automatically summarizes older messages
/// once the conversation grows beyond a hysteresis buffer above the target count,
/// preserving context while reducing message count and token usage.</para>
/// <para><strong>Parameter Semantics (per MEAI <c>SummarizingChatReducer</c>):</strong></para>
/// <list type="bullet">
/// <item><term>SummarizationTargetCount</term><description>How many recent messages to keep verbatim after a reduction.</description></item>
/// <item><term>SummarizationThreshold</term><description>Hysteresis buffer <em>above</em> the target. Reduction triggers when message count &gt; <c>TargetCount + Threshold</c> (NOT when it simply exceeds Threshold). E.g. target=8, threshold=12 → first reduction at 21 messages.</description></item>
/// </list>
/// <para><strong>Configuration Example:</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "HistoryReducer": {
///       "Enabled": true,
///       "SummarizationTargetCount": 8,
///       "SummarizationThreshold": 12
///     }
///   }
/// }
/// </code>
/// <para><strong>Behavior:</strong></para>
/// <list type="bullet">
/// <item><term>Enabled=false</term><description>Returns null, no history management</description></item>
/// <item><term>Enabled=true</term><description>Returns SummarizingChatReducer with configured thresholds</description></item>
/// </list>
/// </remarks>
public class SummarizingChatReducerService
{
    private readonly IConfiguration configuration;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of SummarizingChatReducerService.
    /// </summary>
    /// <param name="configuration">Application configuration for reading HistoryReducer settings</param>
    /// <param name="logger">Logger for diagnostics and monitoring</param>
    public SummarizingChatReducerService(
        IConfiguration configuration,
        ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Creates a SummarizingChatReducer based on Morgana:HistoryReducer configuration.
    /// Returns null if history management is disabled.
    /// </summary>
    /// <param name="chatClient">IChatClient instance for LLM-based summarization</param>
    /// <returns>Configured SummarizingChatReducer or null if disabled</returns>
    /// <remarks>
    /// <para><strong>When Reducer is Triggered:</strong></para>
    /// <para>When non-system message count &gt; <c>TargetCount + Threshold</c> (e.g. 8+12=20, so first
    /// trigger at 21 messages), the reducer automatically:</para>
    /// <list type="number">
    /// <item>Picks an anchor near <c>count - target</c>, preferring a user-role boundary</item>
    /// <item>Summarizes all messages before the anchor into a single summary (stored as
    /// <c>__summary__</c> in the anchor message's <c>AdditionalProperties</c>)</item>
    /// <item>Returns [optional system msg] + [summary as assistant msg] + recent messages from the anchor onward</item>
    /// </list>
    /// <para><strong>Note:</strong> <c>SummarizationThreshold</c> is a hysteresis buffer above
    /// <c>SummarizationTargetCount</c>, NOT an absolute trigger count. See the class-level
    /// remarks for details.</para>
    /// </remarks>
    public IChatReducer? CreateReducer(IChatClient chatClient)
    {
        IConfigurationSection config = configuration.GetSection("Morgana:HistoryReducer");

        if (!config.GetValue("Enabled", true))
        {
            logger.LogInformation("History summarization disabled - no reducer created");
            return null;
        }

        int targetCount = config.GetValue<int>("SummarizationTargetCount", 8);
        int threshold = config.GetValue<int>("SummarizationThreshold", 20);

        SummarizingChatReducer chatReducer = new SummarizingChatReducer(chatClient, targetCount, threshold);

        string? summaryPrompt = config.GetValue<string?>("SummarizationPrompt");
        if (!string.IsNullOrWhiteSpace(summaryPrompt))
            chatReducer.SummarizationPrompt = summaryPrompt;

        logger.LogInformation(
            "Created SummarizingChatReducer: target={TargetCount}, threshold(buffer)={Threshold} → reduction triggers when message count > {Trigger}",
            targetCount, threshold, targetCount + threshold);

        return chatReducer;
    }
}