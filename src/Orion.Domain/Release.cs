using System.Text.RegularExpressions;

namespace Orion.Domain;

public sealed record Repository
{
    public string Owner { get; }
    public string Name { get; }
    public Repository(string owner, string name)
    {
        if (!Regex.IsMatch(owner, @"\A[A-Za-z0-9-]+\z") ||
            !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\z") || name is "." or "..")
            throw new ArgumentException("Invalid GitHub repository.");
        Owner = owner;
        Name = name;
    }
    public override string ToString() => $"{Owner}/{Name}";
}

public sealed record ReleaseAsset(long Id, string Name, Uri Download, long Size, string? Digest);
public sealed record Release(string Tag, Uri Page, IReadOnlyList<ReleaseAsset> Assets);
public sealed record RuntimeInstallation(string Name, string Tag, string Directory);
public sealed record OperationProgress(string Message, double? Fraction = null);
