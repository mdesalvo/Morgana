using System.Reflection;
using Distiller.Interfaces;
using Distiller.Model;
using Distiller.Services;
using Microsoft.Extensions.DependencyInjection;
using Morgana.AI;
using Morgana.AI.Extensions;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Every tool a pass declares against the method that has to answer it.
/// </summary>
/// <remarks>
/// The pairing is validated for real when a pass opens, which is a live call and a client's turn:
/// a tool declared in <c>alembic.json</c> with no method behind it took down every interview this
/// suite has, and the first thing anybody saw was a step that would not open. The same fact is
/// decidable by reading the two, so it is read here instead, for nothing.
/// <para>
/// Names, count and which are optional, because that is exactly what the framework's own adapter
/// checks before it will bind one: a parameter renamed on one side alone is a schema the model can
/// satisfy and the method cannot receive.
/// </para>
/// </remarks>
[Trait("Stage", "Rules")]
public sealed class ToolDeclarationTests
{
    private readonly AlembicHostFixture fixture;

    public ToolDeclarationTests(AlembicHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData(InterviewStep.DomainMapper)]
    [InlineData(InterviewStep.AgentTarget)]
    [InlineData(InterviewStep.AgentPersonality)]
    [InlineData(InterviewStep.AgentToolkit)]
    [InlineData(InterviewStep.AgentTerritory)]
    [InlineData(InterviewStep.AgentInstructions)]
    [InlineData(InterviewStep.AgentFormatting)]
    [InlineData(InterviewStep.DomainColleagues)]
    public void Every_tool_a_pass_declares_is_a_method_of_the_same_shape(InterviewStep pass)
    {
        using IServiceScope scope = fixture.NewScope();
        IAlembicPromptService prompts = scope.ServiceProvider.GetRequiredService<IAlembicPromptService>();

        List<Records.ToolDefinition> declared = prompts.Resolve(pass.ToString())
            .GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>(Constants.PromptProperties.Tools, []);

        Assert.NotEmpty(declared);

        foreach (Records.ToolDefinition tool in declared)
        {
            MethodInfo? method = typeof(InterviewTools).GetMethod(
                tool.Name, BindingFlags.Public | BindingFlags.Instance);

            Assert.True(method is not null,
                $"{pass} declares '{tool.Name}', which InterviewTools has no method for.");

            // The cancellation token some tools take is the framework's, never the model's, so it is
            // not part of what a declaration has to account for.
            ParameterInfo[] parameters = [.. method!.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))];

            Assert.True(parameters.Length == tool.Parameters.Count,
                $"{pass}.{tool.Name} declares {tool.Parameters.Count} parameter(s) and the method takes "
                + $"{parameters.Length}: {string.Join(", ", parameters.Select(p => p.Name))}.");

            foreach ((Records.ToolParameter declaredParameter, ParameterInfo parameter) in tool.Parameters.Zip(parameters))
            {
                Assert.True(string.Equals(declaredParameter.Name, parameter.Name, StringComparison.Ordinal),
                    $"{pass}.{tool.Name} declares '{declaredParameter.Name}' where the method takes '{parameter.Name}'.");

                // A declaration calling a parameter required while the method gives it a default is
                // the pair drifting in the direction nothing catches: the model is told it must send
                // something the method is perfectly happy to do without.
                Assert.True(declaredParameter.Required != parameter.IsOptional,
                    $"{pass}.{tool.Name}.{parameter.Name} is declared "
                    + $"{(declaredParameter.Required ? "required" : "optional")} and the method has it "
                    + $"{(parameter.IsOptional ? "optional" : "required")}.");
            }
        }
    }
}
