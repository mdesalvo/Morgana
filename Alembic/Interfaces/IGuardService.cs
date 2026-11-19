using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Reads what is about to become prose or code of the client's agents before anything else does.
/// Every verdict fails closed: a guard that could not judge a text throws, so nothing passes on the strength of a failure.
/// </summary>
public interface IGuardService
{
    /// <summary>Judges an answer the client has just typed.</summary>
    /// <param name="question">The question the answer replies to; <c>null</c> or blank when there was none.</param>
    /// <param name="answer">The client's answer.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    /// <returns>Whether the answer may reach the interview.</returns>
    Task<GuardVerdict> JudgeAnswerAsync(string? question, string answer, CancellationToken cancellationToken = default);

    /// <summary>Judges a file the client has uploaded.</summary>
    /// <param name="uploadedText">The whole text of the file.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    /// <returns>Whether the file may become part of the domain.</returns>
    Task<GuardVerdict> JudgeUploadAsync(string uploadedText, CancellationToken cancellationToken = default);

    /// <summary>Judges a mock against the descriptions of the tools it implements.</summary>
    /// <param name="contract">The generated half of the tool class, carrying every description.</param>
    /// <param name="target">What the agent exists for; <c>null</c> or blank when none is written.</param>
    /// <param name="mock">The source of the mock.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    /// <returns>Whether the mock does nothing its tools do not declare.</returns>
    Task<GuardVerdict> JudgeMockAsync(string contract, string? target, string mock, CancellationToken cancellationToken = default);
}