namespace Morgana.Messages;

public record ClassificationResult(string Category, string Intent, Dictionary<string, string> Metadata);