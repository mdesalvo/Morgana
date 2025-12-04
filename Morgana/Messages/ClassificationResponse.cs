namespace Morgana.Messages;

public record ClassificationResponse(string Category, string Intent, double Confidence);