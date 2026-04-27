using System.Text.Json;

namespace PolyPilot.Models;

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
