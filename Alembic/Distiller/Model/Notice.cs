namespace Distiller.Model;

/// <summary>
/// What a notice tells the client, in the same three states the validation checks are drawn in.
/// </summary>
public enum NoticeKind
{
    /// <summary>Something was done and stands as asked.</summary>
    Success,

    /// <summary>Nothing broke, but the client has something to complete or look at.</summary>
    Warning,

    /// <summary>Something did not work or stops the domain from going on.</summary>
    Failure,

    /// <summary>Neither good nor bad news: what happened, as the client asked for it.</summary>
    Info
}

/// <summary>
/// One passing message to the client and what kind of news it is.
/// </summary>
public sealed record Notice(string Message, NoticeKind Kind);
