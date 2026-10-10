namespace Morgana.Contracts;

/// <summary>
/// Why Morgana refused to take a call, declared on <see cref="ChannelMessage.ErrorReason"/> by the host and read by
/// every channel. A channel reads it to act on the refusal rather than merely paint it: a spent budget ends the
/// conversation on its side too.
/// </summary>
public static class ChannelErrorReasons
{
    /// <summary>The conversation called too often in one of its windows; it may call again later.</summary>
    public const string RateLimitExceeded = "rate_limit_exceeded";

    /// <summary>The conversation has spent 70% of its dust budget: an advisory, the conversation goes on.</summary>
    public const string DustBudgetLow70 = "dust_budget_low_70";

    /// <summary>The conversation has spent 90% of its dust budget: an advisory, the conversation goes on.</summary>
    public const string DustBudgetLow90 = "dust_budget_low_90";

    /// <summary>The conversation's dust budget is spent: it will take no further turn or command.</summary>
    public const string DustBudgetExhausted = "dust_budget_exhausted";
}