namespace Morgana.AI.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple=false)]
public class HandlesIntentAttribute : Attribute
{
    public string Intent { get; }

    public HandlesIntentAttribute(string intent)
    {
        Intent = intent;
    }
}