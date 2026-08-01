using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Services;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Creates SummarizingChatReducer instances for context window management via LLM-based summarization. Reads Morgana:HistoryReducer
/// config (Enabled, SummarizationTargetCount, SummarizationThreshold). Threshold is hysteresis buffer above target: reduction triggers at
/// count &gt; target+threshold (e.g., target=8, threshold=12 → first reduction at 21 messages). Returns null if disabled.
/// </summary>
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
    /// Creates configured SummarizingChatReducer or null if disabled. When triggered (count &gt; target+threshold),
    /// reducer anchors at user-role boundary, summarizes prior messages into __summary__, returns [system] + [summary] + recent.
    /// </summary>
    public IChatReducer? CreateReducer(IChatClient chatClient)
    {
        IConfigurationSection config = configuration.GetSection("Morgana:HistoryReducer");

        if (!config.GetValue("Enabled", true))
        {
            logger.LogInformation("History summarization disabled - no reducer created");
            return null;
        }

        int targetCount = config.GetValue<int>("SummarizationTargetCount", 8);
        int threshold = config.GetValue<int>("SummarizationThreshold", 12);

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