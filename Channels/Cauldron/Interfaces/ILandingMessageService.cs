namespace Cauldron.Interfaces;

/// <summary>
/// Service for providing landing messages during the loading phase.
/// </summary>
public interface ILandingMessageService
{
    /// <summary>
    /// Gets a landing message using the configured strategy.
    /// </summary>
    string GetLandingMessage();
}
