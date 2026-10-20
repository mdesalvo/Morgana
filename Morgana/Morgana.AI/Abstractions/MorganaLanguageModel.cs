using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Morgana.AI.Abstractions;

/// <summary>
/// One LLM provider: builds the chat client of a single tier from that tier's connection section and options.
/// </summary>
public abstract class MorganaLanguageModel
{
    /// <summary>The name that a tier writes in <c>Morgana:LanguageModels:Tiers:{tier}:Provider</c> to be served by this provider.</summary>
    public abstract string Provider { get; }

    /// <summary>
    /// Refuses a connection that lacks a field that this provider uses.
    /// </summary>
    /// <remarks>
    /// The message opens with the name of the field so that the caller can put the key path of the tier in front of it.
    /// </remarks>
    /// <param name="connectionSection">The <c>Connection</c> section of the tier, which exists when the provider is asked.</param>
    /// <exception cref="InvalidOperationException">A field that the provider uses is missing, empty or still a placeholder.</exception>
    public abstract void ValidateConnection(IConfigurationSection connectionSection);

    /// <summary>Builds the provider client that serves one tier, with no telemetry and no tier defaults around it.</summary>
    /// <param name="connectionSection">The <c>Connection</c> section of the tier: how it reaches the provider.</param>
    /// <param name="options">The model that the tier runs.</param>
    public abstract IChatClient CreateChatClient(IConfigurationSection connectionSection, Records.TierConfiguration options);

    /// <summary>Tells whether an exception thrown by this provider's client is the provider refusing the content itself, a refusal that no retry would pass.</summary>
    /// <param name="exception">The failure that the client of a tier threw.</param>
    public abstract bool IsContentFilterRefusal(Exception exception);

    /// <summary>Refuses a value that is missing, empty or still a placeholder, naming <paramref name="field"/> first.</summary>
    protected static void RequireField(string? value, string field)
    {
        // A blank value or a shipped placeholder means the deployment never supplied the secret: the host must not start on it.
        if (string.IsNullOrWhiteSpace(value) || Constants.SecretOverrides.All.Contains(value))
            throw new InvalidOperationException($"{field} is missing or still a placeholder. Override it via User Secrets or environment variables.");
    }
}

/// <summary>
/// A provider that binds the <c>Connection</c> section of its tier to <typeparamref name="TConnection"/>.
/// </summary>
/// <remarks>
/// A provider author declares a connection record holding exactly the fields that the provider honours and never touches <see cref="IConfiguration"/>.
/// </remarks>
/// <typeparam name="TConnection">The connection shape that only this provider knows.</typeparam>
public abstract class MorganaLanguageModel<TConnection> : MorganaLanguageModel
{
    /// <inheritdoc />
    public sealed override void ValidateConnection(IConfigurationSection connectionSection) =>
        // The provider sees its own connection shape and never the raw section.
        ValidateConnection(Bind(connectionSection));

    /// <inheritdoc />
    public sealed override IChatClient CreateChatClient(IConfigurationSection connectionSection, Records.TierConfiguration options) =>
        // The client is built from the same typed connection that validation saw.
        CreateChatClient(Bind(connectionSection), options);

    /// <summary>Refuses a connection that lacks a field that this provider uses, naming the field first.</summary>
    /// <param name="connection">The connection of the tier as this provider reads it.</param>
    /// <exception cref="InvalidOperationException">A field that the provider uses is missing, empty or still a placeholder.</exception>
    protected abstract void ValidateConnection(TConnection connection);

    /// <summary>Builds the provider client that serves one tier from its typed connection.</summary>
    /// <param name="connection">How the tier reaches the provider.</param>
    /// <param name="options">The model that the tier runs.</param>
    protected abstract IChatClient CreateChatClient(TConnection connection, Records.TierConfiguration options);

    /// <summary>Reads the connection section into the shape that this provider declares.</summary>
    private static TConnection Bind(IConfigurationSection connectionSection) =>
        // ConfigurationLanguageModelService refuses a tier without a Connection section before any provider is asked, so the binder finds one.
        connectionSection.Get<TConnection>()!;
}