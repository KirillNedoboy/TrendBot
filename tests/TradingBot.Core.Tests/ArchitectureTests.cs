using System.Reflection;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void CoreHasNoProjectReferences()
    {
        var projectReferences = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Where(assembly => assembly.Name?.StartsWith("TradingBot.", StringComparison.Ordinal) == true)
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Empty(projectReferences);
    }
}
