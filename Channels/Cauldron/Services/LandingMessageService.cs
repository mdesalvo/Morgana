using Cauldron.Interfaces;

namespace Cauldron.Services;

public class LandingMessageService : ILandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    public LandingMessageService(IConfiguration configuration)
    {
        landingMessages = configuration.GetSection("Cauldron:LandingMessages").Get<string[]>()
                            ?? ["\uD83D\uDD2E Warming up the magic... almost there! \uD83D\uDD2E"];
    }

    /// <summary>
    /// Gets a random landing message from the pool of configured ones.
    /// </summary>
    public string GetLandingMessage()
    {
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}