namespace Cauldron.Interfaces;

/// <summary>Supplies the line shown under the loader while the chat comes up.</summary>
public interface ILandingMessageService
{
    /// <summary>Returns one line of the configured pool.</summary>
    string GetLandingMessage();
}