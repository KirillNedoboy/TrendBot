using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using TradingBot.Core;
using TradingBot.TestFixtures;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CoreFixtureVectorTests
{
    [Fact]
    public void FactoriesProduceFreshObjectsWithStableExpectedValues()
    {
        FillSimulationInput first = CoreFixtureVectors.NewFillInput();
        FillSimulationInput second = CoreFixtureVectors.NewFillInput();

        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        Assert.NotSame(first.InstrumentId, second.InstrumentId);
        Assert.Equal(CoreFixtureVectors.InstrumentValue, first.InstrumentId.Value);
        Assert.Equal("unknown: fee schedule is unavailable", first.Assumptions.Fees);
        Assert.Equal(new UtcTimestamp(CoreFixtureVectors.BaseDateTime),
            CoreFixtureVectors.NewEquivalentOffsetTime());
        Assert.NotEqual(new InstrumentId("btcusdt"), first.InstrumentId);

        foreach (CoreEventComparisonVector vector in CoreFixtureVectors.EventComparisonVectors)
        {
            Assert.Equal(vector.ExpectedRelation,
                MarketEventComparison.Compare(vector.Previous, vector.Candidate));
        }
    }

    [Fact]
    public void DomainAndRiskVectorsPreserveDeclaredInvariants()
    {
        Bar bar = CoreFixtureVectors.NewBar();
        Setup setup = CoreFixtureVectors.NewSetup();
        Position position = CoreFixtureVectors.NewPosition();
        RiskMathVector risk = CoreFixtureVectors.NewRiskMathVector();

        Assert.True(bar.OpenTime.Value < bar.CloseTime.Value);
        Assert.InRange(setup.AnchorPrice.Value, setup.LowSinceAnchor.Value,
            setup.HighSinceAnchor.Value);
        Assert.True(position.UpdatedAt.Value >= position.OpenedAt.Value);
        Assert.Equal(risk.ExpectedRiskBudget, risk.Result.RiskBudget.Amount);
        Assert.Equal(risk.ExpectedLossPerUnit, risk.Result.LossPerUnit.Amount);
        Assert.Equal(risk.ExpectedRawQuantity, risk.Result.RawQuantity.Value);
        Assert.Equal(risk.ExpectedNotionalExposure, risk.Result.NotionalExposure.Amount);
    }

    [Fact]
    public void CausalAndFillVectorsExposeTheirDeclaredExpectedValues()
    {
        CausalOrderingVector causal = CoreFixtureVectors.CausalOrdering;
        string[] orderedIds = causal.Events
            .Where(causal.EventCursor.IsEligible)
            .OrderBy(static item => item,
                Comparer<CausalMarketEvent>.Create(CausalMarketEventOrdering.Compare))
            .Select(static item => item.Payload.EventId)
            .ToArray();

        Assert.Equal(causal.ExpectedEventIds, orderedIds);

        FillSimulationResult result = CoreFixtureVectors.NewFillResult();
        Assert.Equal(CoreFixtureVectors.FillVersionValue, result.Version.Value);
        Assert.Equal(result.Input.RequestedQuantity.Value,
            result.Fills.Sum(static fill => fill.FilledQuantity.Value));
        Assert.All(result.Fills, fill => Assert.InRange(fill.FilledQuantity.Value,
            0m, result.Input.RequestedQuantity.Value));
    }

    [Fact]
    public void ImmutableCollectionVectorsDefensivelyCopyCallerOwnedLists()
    {
        ImmutableCollectionVector vector = CoreFixtureVectors.NewImmutableCollectionVector();

        vector.SourceBids.Add(new BookLevel(new Price(99m), new Quantity(4m)));
        vector.SourceAsks.Clear();

        Assert.Single(vector.Value.Bids);
        Assert.Equal(100m, vector.Value.Bids[0].Price.Value);
        Assert.Single(vector.Value.Asks);
        Assert.Equal(101m, vector.Value.Asks[0].Price.Value);
        Assert.Equal(CoreFixtureVectors.NewDecision(),
            CoreFixtureVectors.NewDecision());
        Assert.Equal(CoreFixtureVectors.NewRiskDecision(),
            CoreFixtureVectors.NewRiskDecision());
    }

    [Fact]
    public void ExceptionVectorsDeclareExactExceptionAndParameterExpectations()
    {
        Assert.NotEmpty(CoreFixtureVectors.ExceptionVectors);

        foreach (CoreExceptionVector vector in CoreFixtureVectors.ExceptionVectors)
        {
            Exception? exception = Record.Exception(vector.Invoke);

            Assert.NotNull(exception);
            Assert.True(vector.ExceptionType.IsAssignableFrom(exception!.GetType()),
                $"{vector.Name} expected {vector.ExceptionType.Name}, got {exception.GetType().Name}.");
            Assert.Equal(vector.ParameterName, (exception as ArgumentException)?.ParamName);
        }
    }

    [Fact]
    public void JsonRoundTripVectorsAreStrictDeterministicAndCultureIndependent()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            foreach (CoreJsonRoundTripVector vector in CoreFixtureVectors.JsonRoundTripVectors)
            {
                object first = vector.Create();
                object second = vector.Create();
                Assert.Equal(first, second);
                Assert.NotSame(first, second);

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                string enUsJson = vector.Serialize(first);

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
                string frJson = vector.Serialize(vector.Create());

                Assert.Equal(enUsJson, frJson);
                Assert.Equal(first, vector.Deserialize(frJson));
                Assert.DoesNotContain('\n', frJson);
                Assert.DoesNotContain('\r', frJson);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void JsonInvalidVectorsRejectMissingUnknownAndInvalidFields()
    {
        Assert.Contains(CoreFixtureVectors.JsonInvalidVectors,
            vector => vector.Name.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(CoreFixtureVectors.JsonInvalidVectors,
            vector => vector.Name.Contains("unknown", StringComparison.Ordinal));
        Assert.Contains(CoreFixtureVectors.JsonInvalidVectors,
            vector => vector.Name.Contains("invalid", StringComparison.Ordinal));

        foreach (CoreJsonInvalidVector vector in CoreFixtureVectors.JsonInvalidVectors)
        {
            Exception? exception = Record.Exception(() => vector.Deserialize(vector.Json));

            Assert.NotNull(exception);
            Assert.True(vector.ExceptionType.IsAssignableFrom(exception!.GetType()),
                $"{vector.Name} expected {vector.ExceptionType.Name}, got {exception.GetType().Name}.");
        }
    }

    [Fact]
    public void FixtureCatalogUsesNoRuntimeAssemblyOrAuthorizationSurface()
    {
        Type fixtureType = typeof(CoreFixtureVectors);
        Assert.Equal("TradingBot.TestFixtures", fixtureType.Namespace);
        Assert.DoesNotContain(fixtureType.Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "TradingBot.Exchange.Binance" or "TradingBot.Infrastructure"
                or "TradingBot.App");

        Type[] fillTypes =
        [typeof(IFillSimulator), typeof(FillSimulationInput), typeof(FillSimulationResult),
            typeof(FillSimulationAssumptions), typeof(FillSimulationVersion), typeof(ResearchFill)];
        string[] forbiddenNames =
        ["OrderIntent", "RiskDecision", "IRiskMathCalculator", "RiskMathInput", "RiskMathResult",
            "Authorize", "Execute", "Executor"];

        foreach (Type fillType in fillTypes)
        {
            string surface = string.Join("|", fillType.GetMembers()
                .Select(member => member.Name)
                .Concat(fillType.GetProperties().Select(property => property.PropertyType.FullName ?? ""))
                .Concat(fillType.GetMethods().SelectMany(method => new[]
                {
                    method.ReturnType.FullName ?? ""
                }.Concat(method.GetParameters().Select(parameter =>
                    parameter.ParameterType.FullName ?? "")))));

            Assert.DoesNotContain(forbiddenNames, name => surface.Contains(name,
                StringComparison.Ordinal));
        }

        Assert.All(typeof(CoreFixtureVectors).GetProperties(BindingFlags.Public | BindingFlags.Static),
            property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void FixtureCatalogIsLinkedOnlyIntoTheTwoTestProjects()
    {
        string coreProjectPath = FindRepositoryFile("tests/TradingBot.Core.Tests/TradingBot.Core.Tests.csproj");
        string replayProjectPath = FindRepositoryFile("tests/TradingBot.Replay.Tests/TradingBot.Replay.Tests.csproj");
        string runtimeProjectPath = FindRepositoryFile("src/TradingBot.Core/TradingBot.Core.csproj");
        const string includePath = @"..\Fixtures\CoreFixtureVectors.cs";

        foreach (string projectPath in new[] { coreProjectPath, replayProjectPath })
        {
            XDocument project = XDocument.Load(projectPath);
            Assert.Contains(includePath, project.Descendants()
                .Where(element => element.Name.LocalName == "Compile")
                .Select(element => (string?)element.Attribute("Include")));
        }

        XDocument runtimeProject = XDocument.Load(runtimeProjectPath);
        Assert.DoesNotContain(includePath, runtimeProject.Descendants()
            .Where(element => element.Name.LocalName == "Compile")
            .Select(element => (string?)element.Attribute("Include")));
    }

    [Fact]
    public void ProductionProjectGraphMatchesApprovedModuleBoundaries()
    {
        Dictionary<string, string[]> expectedReferences = new(StringComparer.Ordinal)
        {
            ["src/TradingBot.Core/TradingBot.Core.csproj"] = [],
            ["src/TradingBot.Exchange.Binance/TradingBot.Exchange.Binance.csproj"] =
                ["TradingBot.Core"],
            ["src/TradingBot.Infrastructure/TradingBot.Infrastructure.csproj"] =
                ["TradingBot.Core"],
            ["src/TradingBot.Backtesting/TradingBot.Backtesting.csproj"] =
                ["TradingBot.Core"],
            ["src/TradingBot.App/TradingBot.App.csproj"] =
                ["TradingBot.Backtesting", "TradingBot.Core", "TradingBot.Exchange.Binance",
                    "TradingBot.Infrastructure"],
            ["tools/TradingBot.DataTool/TradingBot.DataTool.csproj"] =
                ["TradingBot.Backtesting", "TradingBot.Infrastructure"]
        };

        foreach ((string relativePath, string[] expected) in expectedReferences)
        {
            Assert.Equal(expected, ReadProjectReferences(relativePath));
        }
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

    private static string[] ReadProjectReferences(string relativeProjectPath)
    {
        string projectPath = FindRepositoryFile(relativeProjectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        XDocument project = XDocument.Load(projectPath);

        return project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element =>
            {
                string include = (string?)element.Attribute("Include")
                    ?? throw new InvalidDataException(
                        $"Project reference in '{relativeProjectPath}' has no Include attribute.");
                string targetPath = Path.GetFullPath(Path.Combine(projectDirectory, include));
                if (!File.Exists(targetPath))
                {
                    throw new FileNotFoundException(
                        $"Project reference target '{include}' does not exist.", targetPath);
                }

                return Path.GetFileNameWithoutExtension(targetPath);
            })
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }
}
