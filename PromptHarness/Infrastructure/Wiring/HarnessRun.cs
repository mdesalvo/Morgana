namespace PromptHarness.Infrastructure.Wiring;

/// <summary>The runtime values of one run that the components built over the host under test read: where it answers, the key its harness issuer signs with and where its databases live.</summary>
public sealed record HarnessRun(string BaseAddress, string IssuerKey, string StoragePath);
