using System.Reflection;
using Xunit;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Boots the real entry point with some configuration replaced, expecting a refusal.
/// </summary>
internal static class DoomedHost
{
    /// <summary>
    /// How long a doomed host is given to refuse. The checks run before anything is bound, so a
    /// boot that is still alive after this did not refuse at all.
    /// </summary>
    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Boots the host with the given declarations replaced, expecting it to refuse.
    /// </summary>
    /// <remarks>
    /// The overrides are applied to this process's environment, which is how the host reads its
    /// configuration, then put back whatever the outcome: the live host beside it read its own
    /// configuration when it started, so what moves here reaches only the boot about to happen.
    /// Test classes in this assembly never run at the same time, which is what makes that safe.
    /// </remarks>
    /// <param name="overrides">Configuration keys to replace, in environment-variable form.</param>
    /// <returns>The exception the host refused with.</returns>
    internal static Exception AssertRefusesToBoot(params (string Key, string Value)[] overrides)
    {
        (string Key, string? Value)[] restore =
            [.. overrides.Select(entry => (entry.Key, Environment.GetEnvironmentVariable(entry.Key)))];

        foreach ((string key, string value) in overrides)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            Exception? refusal = BootAndWaitForRefusal();

            Assert.True(refusal is not null,
                "The host booted a trust configuration it was supposed to refuse: "
                + string.Join(", ", overrides.Select(entry => $"{entry.Key}={entry.Value}")));

            return refusal!;
        }
        finally
        {
            foreach ((string key, string? value) in restore)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Runs the host's entry point on a thread of its own, handing back what it threw.
    /// </summary>
    /// <remarks>
    /// A refusal is fatal to the boot, so it arrives as an exception rather than a status. A boot
    /// that does not refuse would serve forever, which is why the wait is bounded rather than
    /// joined: the port is left to the server to pick, so nothing collides with the live host if
    /// this one does start.
    /// </remarks>
    /// <returns>What the boot threw, or <c>null</c> when it was still running when time ran out.</returns>
    private static Exception? BootAndWaitForRefusal()
    {
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

        string[] arguments =
        [
            "--urls", "http://127.0.0.1:0",
            "--environment", "Development",
            "--contentRoot", AppContext.BaseDirectory,
            "--applicationName", typeof(Program).Assembly.GetName().Name!
        ];

        Exception? thrown = null;

        Thread boot = new Thread(() =>
        {
            try
            {
                if (entryPoint.Invoke(null, [arguments]) is Task hostTask)
                    hostTask.GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex)
            {
                // Reflection wraps what the boot threw; the refusal itself is what a deployer reads.
                thrown = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-harness-doomed-host"
        };

        boot.Start();
        boot.Join(RefusalBudget);

        return thrown;
    }
}
