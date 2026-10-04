using System.Text.Json;
using Orion.Domain;

namespace Orion.Infrastructure.Content;

/// <summary>Creates only user storage, inside the private prefix. Never guesses an Xbox storage ID.</summary>
internal static class InstanceContentDirectories
{
    internal static void Ensure(string root)
    {
        var wineUsers = ContentFiles.Safe(root, "prefix/drive_c/users");
        var dataRoots = new List<string>();
        if (Directory.Exists(wineUsers))
            foreach (var user in Directory.EnumerateDirectories(wineUsers))
            foreach (var edition in new[] { "Minecraft Bedrock", "Minecraft Bedrock Preview" })
            {
                var path = ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(user, "AppData/Roaming", edition, "Users")));
                if (Directory.Exists(path)) dataRoots.Add(path);
            }

        if (dataRoots.Count == 0)
        {
            var manifest = ContentFiles.Safe(root, "instance.json");
            // Fixtures/unknown directories are not instances and must not acquire invented game paths.
            if (!File.Exists(manifest)) return;
            var instance = JsonSerializer.Deserialize<GameInstance>(ContentFiles.ReadSmall(manifest), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Invalid instance metadata.");
            if (Path.GetFileName(root) != instance.Id.ToString("N")) throw new InvalidDataException("Instance identity mismatch.");
            var username = Environment.UserName;
            if (string.IsNullOrWhiteSpace(username) || username is "." or ".." || username.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidDataException("Invalid Wine user name.");
            var userPath = ContentFiles.Safe(wineUsers, username);
            if (!Directory.Exists(userPath) && Directory.Exists(wineUsers))
            {
                var users = Directory.EnumerateDirectories(wineUsers)
                    .Where(p => Path.GetFileName(p) is not ("Public" or "Default" or "All Users" or "Default User"))
                    .ToArray();
                if (users.Length > 1) throw new InvalidOperationException("Multiple Wine user folders found. Start the instance once to identify its game storage.");
                if (users.Length == 1) userPath = ContentFiles.Safe(wineUsers, Path.GetFileName(users[0]));
            }
            var edition = instance.Channel.Equals("Preview", StringComparison.OrdinalIgnoreCase) ? "Minecraft Bedrock Preview" : "Minecraft Bedrock";
            var data = ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(userPath, "AppData/Roaming", edition, "Users")));
            Directory.CreateDirectory(data); dataRoots.Add(data);
        }

        foreach (var users in dataRoots)
        {
            foreach (var kind in new[] { "behavior_packs", "resource_packs" })
                Directory.CreateDirectory(ContentFiles.Safe(users, "Shared/games/com.mojang/" + kind));
            foreach (var player in Directory.EnumerateDirectories(users))
            {
                if (Path.GetFileName(player) == "Shared") continue;
                Directory.CreateDirectory(ContentFiles.Safe(users, Path.GetRelativePath(users, Path.Combine(player, "games/com.mojang/minecraftWorlds"))));
            }
        }
    }
}
