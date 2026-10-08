using Alembic.Services;
using Morgana.AI;

namespace Alembic.Model;

/// <summary>
/// The tools that each pass of the interview is offered, declared as the framework declares a workflow step.
/// </summary>
/// <remarks>
/// Passes specialise a common base by adding tools only. Every name is a <c>nameof</c>, so a tool that does not exist fails the build.
/// </remarks>
public static class InterviewSteps
{
    /// <summary>
    /// The tools that every pass holds: the facts about the business, the placing, the button and the signal that the pass is settled.
    /// </summary>
    private static readonly string[] EveryInterviewStep =
    [
        nameof(InterviewTools.NoteDomainFact),
        nameof(InterviewTools.DropDomainFact),
        nameof(InterviewTools.RecallAgent),
        nameof(InterviewTools.SetStepPlacing),
        nameof(InterviewTools.SetChoice),
        nameof(InterviewTools.SetPassCompleted)
    ];

    /// <summary>
    /// The tools that every pass standing on one agent holds: the base plus what is written of the agent and what was found about it.
    /// </summary>
    private static readonly string[] EveryAgentStep =
    [
        .. EveryInterviewStep,
        nameof(InterviewTools.GetAgentSoFar),
        nameof(InterviewTools.GetFindings)
    ];

    /// <summary>
    /// The tools of the DomainMapper pass.
    /// </summary>
    public static readonly Records.WorkflowStep DomainMapper = new(
        nameof(InterviewStep.DomainMapper),
        [
            .. EveryInterviewStep,
            nameof(InterviewTools.DeclareIntent),
            nameof(InterviewTools.DropIntent),
            nameof(InterviewTools.GetDomainMap),
            nameof(InterviewTools.GetExistingIntents),
            nameof(InterviewTools.SetExample),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The tools of the AgentTarget pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentTarget = new(
        nameof(InterviewStep.AgentTarget),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.GetExistingIntents),
            nameof(InterviewTools.SetAgentTarget),
            nameof(InterviewTools.SetExample),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The tools of the AgentPersonality pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentPersonality = new(
        nameof(InterviewStep.AgentPersonality),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.GetComposedPrompt),
            nameof(InterviewTools.SetAgentPersonality),
            nameof(InterviewTools.SetTraits),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The tools of the AgentToolkit pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentToolkit = new(
        nameof(InterviewStep.AgentToolkit),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.DeclareTool),
            nameof(InterviewTools.DropTool),
            nameof(InterviewTools.DropToolParameter),
            nameof(InterviewTools.DropToolReturn),
            nameof(InterviewTools.GetToolkit),
            nameof(InterviewTools.SetExample),
            nameof(InterviewTools.SetToolParameter),
            nameof(InterviewTools.SetToolReturn),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The tools of the AgentWorkflows pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentWorkflows = new(
        nameof(InterviewStep.AgentWorkflows),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.AddEdge),
            nameof(InterviewTools.AddFailureEdge),
            nameof(InterviewTools.DeclareWorkflow),
            nameof(InterviewTools.DropEdge),
            nameof(InterviewTools.DropWorkflow),
            nameof(InterviewTools.DropWorkflowStep),
            nameof(InterviewTools.GetToolkit),
            nameof(InterviewTools.GetWorkflows),
            nameof(InterviewTools.SetExample),
            nameof(InterviewTools.SetWorkflowStep)
        ]);

    /// <summary>
    /// The tools of the AgentTerritory pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentTerritory = new(
        nameof(InterviewStep.AgentTerritory),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.GetAgentCard),
            nameof(InterviewTools.GetExistingIntents),
            nameof(InterviewTools.GetToolkit),
            nameof(InterviewTools.SetAgentTerritory),
            nameof(InterviewTools.SetExample),
            nameof(InterviewTools.SetIntentDescription),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The tools of the AgentInstructions pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentInstructions = new(
        nameof(InterviewStep.AgentInstructions),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.GetComposedPrompt),
            nameof(InterviewTools.GetToolkit),
            nameof(InterviewTools.SetAgentInstructions),
            nameof(InterviewTools.SetExample)
        ]);

    /// <summary>
    /// The tools of the AgentFormatting pass.
    /// </summary>
    public static readonly Records.WorkflowStep AgentFormatting = new(
        nameof(InterviewStep.AgentFormatting),
        [
            .. EveryAgentStep,
            nameof(InterviewTools.GetComposedPrompt),
            nameof(InterviewTools.GetToolkit),
            nameof(InterviewTools.SetAgentFormatting),
            nameof(InterviewTools.SetExample)
        ]);

    /// <summary>
    /// The tools of the DomainColleagues pass.
    /// </summary>
    public static readonly Records.WorkflowStep DomainColleagues = new(
        nameof(InterviewStep.DomainColleagues),
        [
            .. EveryInterviewStep,
            nameof(InterviewTools.DeclareConsultation),
            nameof(InterviewTools.DropConsultation),
            nameof(InterviewTools.GetConsultations),
            nameof(InterviewTools.GetDomainAgents),
            nameof(InterviewTools.ShowWhatIsWritten)
        ]);

    /// <summary>
    /// The step that declares the tools of one interview pass.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The pass has no declared step.</exception>
    public static Records.WorkflowStep Of(InterviewStep step) => step switch
    {
        InterviewStep.DomainMapper => DomainMapper,
        InterviewStep.AgentTarget => AgentTarget,
        InterviewStep.AgentPersonality => AgentPersonality,
        InterviewStep.AgentToolkit => AgentToolkit,
        InterviewStep.AgentWorkflows => AgentWorkflows,
        InterviewStep.AgentTerritory => AgentTerritory,
        InterviewStep.AgentInstructions => AgentInstructions,
        InterviewStep.AgentFormatting => AgentFormatting,
        InterviewStep.DomainColleagues => DomainColleagues,
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "No tools are declared for this interview step.")
    };
}
