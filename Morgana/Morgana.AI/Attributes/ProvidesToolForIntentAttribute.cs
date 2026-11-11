namespace Morgana.AI.Attributes;

/// <summary>Marks a <see cref="Tools.MorganaTool"/> class as the domain tools of the agent that handles an intent.</summary>
// Every public instance method that the class itself declares is a tool, described by its [Description] and by those of
// its parameters; a parameter whose value the user or the context supplies also carries [ToolParameter].
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ProvidesToolForIntentAttribute : Attribute
{
    /// <summary>The intent whose agent receives these tools, as <see cref="HandlesIntentAttribute"/> names it.</summary>
    public string Intent { get; }

    /// <summary>Declares the intent whose agent receives these tools.</summary>
    /// <param name="intent">The intent's name as the agent's <see cref="HandlesIntentAttribute"/> declares it.</param>
    /// <exception cref="ArgumentException">The intent is null or blank.</exception>
    public ProvidesToolForIntentAttribute(string intent)
    {
        // Tools for no intent would reach no agent, so the declaration fails where it is written.
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("Intent cannot be null or empty", nameof(intent));

        // The tool registry finds the class through this name when the agent is built.
        Intent = intent;
    }
}