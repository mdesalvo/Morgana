using Microsoft.Extensions.AI;

namespace Morgana.AI.Abstractions;

/// <summary>
/// One LLM provider: builds the chat client of a single tier from that tier's connection and options.
/// </summary>
public abstract class MorganaLanguageModel
{
    /// <summary>Builds the provider client that serves one tier, with no telemetry and no tier defaults around it.</summary>
    /// <param name="connection">How the tier reaches the provider.</param>
    /// <param name="options">The model that the tier runs.</param>
    public abstract IChatClient CreateChatClient(Records.TierConnection connection, Records.TierConfiguration options);

    /// <summary>
    /// True when the provider honours a request naming the one tool that the model must call.
    /// </summary>
    /// <remarks>True for every provider whose API names a required tool; a provider without one overrides it.</remarks>
    public virtual bool CanForceToolCall => true;

    /// <summary>
    /// Refuses a connection that lacks a field that this provider uses.
    /// </summary>
    /// <remarks>
    /// The message opens with the name of the field so that the caller can put the key path of the tier in front of it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A field that the provider uses is missing, empty or still a placeholder.</exception>
    public abstract void ValidateConnection(Records.TierConnection connection);

    /// <summary>Refuses a value that is missing, empty or still a placeholder, naming <paramref name="field"/> first.</summary>
    protected static void RequireField(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || Constants.SecretOverrides.All.Contains(value))
            throw new InvalidOperationException($"{field} is missing or still a placeholder. Override it via User Secrets or environment variables.");
    }
}