namespace Cauldron.Services;

public class MorganaLandingMessageService
{
    private readonly string[] landingMessages;
    private readonly Random random = new Random();

    public MorganaLandingMessageService(IConfiguration configuration)
    {
        landingMessages = configuration.GetSection("Cauldron:LandingMessages").Get<string[]>()
                            ?? ["\uD83D\uDD2E Warming up the magic... almost there! \uD83D\uDD2E"];
    }

    public string GetRandomLandingMessage()
    {
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
}