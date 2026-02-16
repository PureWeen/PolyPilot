using System;
using System.Reflection;
using GitHub.Copilot.SDK;

class Program
{
    static void Main()
    {
        var type = typeof(CopilotClientOptions);
        Console.WriteLine($"Type: {type.FullName}");

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var isPublic = prop.GetMethod?.IsPublic == true;
            Console.WriteLine($"Property: {prop.Name} ({prop.PropertyType.Name}) - Public: {isPublic}");
        }

        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            Console.WriteLine($"Constructor: {ctor}");
        }
    }
}