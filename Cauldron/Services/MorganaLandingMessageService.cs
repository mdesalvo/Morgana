namespace Cauldron.Services;

public class MorganaLandingMessageService
{
    private readonly string[] landingMessages;
    private readonly string[] resumingMessages;
    private readonly Random random = new Random();

    public MorganaLandingMessageService(IConfiguration configuration)
    {
        landingMessages = configuration.GetSection("Morgana:LandingMessages").Get<string[]>()
                            ?? ["✨ A pinch of magic and I'll be ready... ✨"];
        resumingMessages = configuration.GetSection("Morgana:ResumingMessages").Get<string[]>()
                           ?? ["🔮 The circle is open once more. We resume where the spell paused. 🔮"];
    }

    public string GetRandomLandingMessage()
    {
        lock (random)
        {
            return landingMessages[random.Next(landingMessages.Length)];
        }
    }
    
    public string GetRandomResumingMessage()
    {
        lock (random)
        {
            return resumingMessages[random.Next(resumingMessages.Length)];
        }
    }
}