using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Morgana.Framework.Services;

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
/// when conversation exceeds a threshold, preserving context while reducing message count and token usage.</para>
/// <para><strong>Configuration Example:</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "HistoryReducer": {
///       "Enabled": true,
///       "SummarizationTargetCount": 8
///       "SummarizationThreshold": 20
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
    /// <para>When conversation message count exceeds SummarizationThreshold (e.g., 20 messages),
    /// the reducer automatically:</para>
    /// <list type="number">
    /// <item>Takes older messages (count - target, e.g., messages 1-12)</item>
    /// <item>Summarizes them using the provided chatClient into 1-2 summary messages</item>
    /// <item>Returns [Summary] + recent messages (e.g., messages 13-20)</item>
    /// </list>
    /// <para><strong>Important:</strong></para>
    /// <para>The reducer only affects what is sent to the LLM. The full conversation history
    /// is still persisted to SQLite and displayed in the UI. This is transparent to the user.</para>
    /// <para><strong>Performance Characteristics:</strong></para>
    /// <list type="bullet">
    /// <item>First 20 messages: No overhead, full context preserved</item>
    /// <item>Message 21+: One-time summarization cost, then reduced context for all subsequent messages</item>
    /// <item>Message 34+: Re-summarization (summarizes messages 1-26 fresh, no incremental summary)</item>
    /// </list>
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
            $"Created SummarizingChatReducer: threshold={threshold} messages, target={targetCount} messages");

        return chatReducer;
    }
}