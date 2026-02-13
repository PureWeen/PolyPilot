using GitHub.Copilot.SDK;
using System.Reflection;

Console.WriteLine("=== CopilotSession Methods (model-related) ===");
foreach (var method in typeof(CopilotSession).GetMethods(BindingFlags.Public | BindingFlags.Instance))
{
    if (method.Name.ToLower().Contains("model") || method.Name.ToLower().Contains("set"))
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {method.Name}({parameters}) -> {method.ReturnType.Name}");
    }
}

Console.WriteLine("\n=== MessageOptions Properties ===");
foreach (var prop in typeof(MessageOptions).GetProperties())
{
    Console.WriteLine($"  {prop.Name}: {prop.PropertyType.Name} (settable: {prop.CanWrite})");
}

Console.WriteLine("\n=== SessionConfig Properties ===");
foreach (var prop in typeof(SessionConfig).GetProperties())
{
    Console.WriteLine($"  {prop.Name}: {prop.PropertyType.Name} (settable: {prop.CanWrite})");
}
