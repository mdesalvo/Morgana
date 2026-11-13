using System.Reflection;
using Xunit;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>Boots the real entry point with some configuration replaced, expecting a refusal.</summary>
internal static class DoomedHost
{
    /// <summary>
    /// How long a doomed host is given to refuse.
    /// The checks run before anything is bound, so a boot still alive after this did not refuse at all.
    /// </summary>
    private static readonly TimeSpan RefusalBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Boots the host with the given declarations replaced, expecting it to refuse.
    /// The overrides go into this process's environment and are put back whatever the outcome: the live host read its configuration at its own start,
    /// so only the boot about to happen sees them, which is safe because the test classes of this assembly never run at the same time.
    /// </summary>
    /// <param name="overrides">Configuration keys to replace, in environment-variable form.</param>
    /// <returns>The exception the host refused with.</returns>
    internal static Exception AssertRefusesToBoot(params (string Key, string Value)[] overrides)
    {
        // The values the environment held before, so each key goes back exactly as it was.
        (string Key, string? Value)[] restore =
            [.. overrides.Select(entry => (entry.Key, Environment.GetEnvironmentVariable(entry.Key)))];

        // The host reads its configuration from the environment, so the replaced declarations reach the boot this way.
        foreach ((string key, string value) in overrides)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            // What the boot threw; nothing when it was still serving when time ran out.
            Exception? refusal = BootAndWaitForRefusal();

            // A boot that did not refuse names the configuration it should have refused.
            Assert.True(refusal is not null,
                "The host booted a trust configuration it was supposed to refuse: "
                + string.Join(", ", overrides.Select(entry => $"{entry.Key}={entry.Value}")));

            // The refusal, for the test to assert that it names the fault.
            return refusal!;
        }
        finally
        {
            // Every key goes back, so a later test or boot reads the configuration it would have read anyway.
            foreach ((string key, string? value) in restore)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Runs the host's entry point on a thread of its own, handing back what it threw, since a refusal is fatal to the boot.
    /// A boot that does not refuse would serve forever, so the wait is bounded; the server picks its own port, so such a boot collides with nothing.
    /// </summary>
    /// <returns>What the boot threw; <c>null</c> when it was still running when time ran out.</returns>
    private static Exception? BootAndWaitForRefusal()
    {
        // Morgana.Web's own Program, booted exactly as a deployment boots it.
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

        // The same arguments the fixture boots its own host with, on a port the system picks.
        string[] arguments =
        [
            "--urls", "http://127.0.0.1:0",
            "--environment", "Development",
            "--contentRoot", AppContext.BaseDirectory,
            "--applicationName", typeof(Program).Assembly.GetName().Name!
        ];

        // Set by the boot thread when the host throws.
        Exception? thrown = null;

        // A background thread, so a boot that never refuses does not keep the test process alive.
        Thread boot = new Thread(() =>
        {
            try
            {
                // The entry point is asynchronous, so its task carries whatever the boot throws.
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
                // A refusal thrown through the task arrives unwrapped.
                thrown = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-harness-doomed-host"
        };

        // The boot is given its budget to refuse, never joined for longer.
        boot.Start();
        boot.Join(RefusalBudget);

        // Null when the boot was still alive, which the caller reads as a configuration that was not refused.
        return thrown;
    }
}