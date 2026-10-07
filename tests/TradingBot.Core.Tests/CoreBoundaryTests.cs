using System.Xml.Linq;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CoreBoundaryTests
{
    [Fact]
    public void CoreProjectHasNoPackageOrProjectReferences()
    {
        string projectPath = FindRepositoryFile("src/TradingBot.Core/TradingBot.Core.csproj");
        XDocument project = XDocument.Load(projectPath);

        string[] forbiddenReferences = project.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(element => element.Name.LocalName)
            .ToArray();

        Assert.Empty(forbiddenReferences);
    }

    [Fact]
    public void CoreAssemblyReferencesOnlyBclAssemblies()
    {
        string[] nonBclReferences = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies()
            .Where(assembly => !IsBclAssembly(assembly.Name))
            .Select(assembly => assembly.Name ?? "<unnamed>")
            .ToArray();

        Assert.Empty(nonBclReferences);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private static bool IsBclAssembly(string? name)
    {
        return name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or "Microsoft.VisualBasic.Core"
            || name?.StartsWith("System.", StringComparison.Ordinal) == true;
    }
}
