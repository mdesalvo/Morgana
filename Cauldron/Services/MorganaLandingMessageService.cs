namespace Cauldron.Services;

public class MorganaLandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    public MorganaLandingMessageService(IConfiguration configuration)
    {
        landingMessages = configuration.GetSection("Morgana:LandingMessages").Get<string[]>()
                            ?? ["✨ A pinch of magic and I'll be ready... ✨"];
    }

    public string GetRandomMessage()
    {
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}