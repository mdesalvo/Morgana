namespace Morgana.AI.Attributes;

/// <summary>
/// Marks a <see cref="Abstractions.MorganaAgent"/> class as the one agent that handles an intent.
/// The intent must match an Intents entry in agents.json and at most one agent may claim it:
/// both directions are validated at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class HandlesIntentAttribute : Attribute
{
    /// <summary>The intent that this agent handles, matched ignoring case.</summary>
    public string Intent { get; }

    /// <summary>Declares the intent that this agent handles.</summary>
    /// <param name="intent">The intent's name as agents.json declares it.</param>
    public HandlesIntentAttribute(string intent)
    {
        // The router reaches the agent through this name once the classifier has chosen it.
        Intent = intent;
    }
}