using Orion.Domain;
using Orion.Infrastructure.Processes;

namespace Orion.Infrastructure.Games;

/// <summary>Builds argument vectors without a shell. Only the game process receives custom values.</summary>
public static class InstanceLaunchPlan
{
    public static Dictionary<string, string?> Environment(InstanceLaunchOptions options, IReadOnlyDictionary<string, string?> defaults)
    {
        options.ValidateLaunchInputs();
        var result = new Dictionary<string, string?>(defaults, StringComparer.Ordinal);
        foreach (var (name, value) in options.Environment) result[name] = value;
        return result;
    }

    public static ProcessCommand Create(string xodus, string game, string wine, string executable, string workingDirectory,
        InstanceLaunchOptions options, IReadOnlyDictionary<string, string?> environment)
    {
        options.ValidateLaunchInputs();
        var arguments = XodusGameCommands.Play(game, wine, executable, options.Arguments);
        // Display preferences are saved placeholders, not launch instructions.
        var template = LaunchCommandTemplate.Parse(options.LaunchCommand);
        string[] command = [.. template[..^1], xodus, .. arguments];
        return new(command[0], command[1..], workingDirectory, environment);
    }
}
