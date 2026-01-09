namespace Morgana.AI.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class UsesMCPServersAttribute : Attribute
{
    public string[] ServerNames { get; }

    public UsesMCPServersAttribute(params string[] serverNames)
    {
        ServerNames = serverNames ?? [];
    }
}