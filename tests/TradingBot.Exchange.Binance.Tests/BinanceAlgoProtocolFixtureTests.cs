using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinanceAlgoProtocolFixtureTests
{
    [Fact]
    public void ParsesConfirmedEnvelopeAndPreservesOpaqueOrderAndCausalFields()
    {
        UtcTimestamp receive = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000000100));
        UtcTimestamp available = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000000110));
        BinanceAlgoUpdateObservation observation = BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.AlgoUpdate(algoId: 7001, status: "NEW",
                eventTime: 1700000000123, transactionTime: 1700000000120),
            new BinanceAlgoObservationMetadata("session-a", 4, receive, available));

        Assert.Equal("ALGO_UPDATE", observation.EventType);
        Assert.Equal(1700000000123, observation.EventTimeMilliseconds);
        Assert.Equal(1700000000120, observation.TransactionTimeMilliseconds);
        Assert.Equal(receive, observation.ReceiveTime);
        Assert.Equal(available, observation.AvailabilityTime);
        Assert.Equal("session-a", observation.SessionId);
        Assert.Equal(4, observation.StableSequence);
        Assert.Equal(7001, observation.OpaqueOrder.GetProperty("algoId").GetInt64());
        Assert.Equal("NEW", observation.OpaqueOrder.GetProperty("status").GetString());
        Assert.Equal(("session-a", 4L), observation.EventIdentity);
    }

    [Fact]
    public void SameAlgoIdTransitionsRemainDistinctFromRepeatedPayload()
    {
        BinanceAlgoUpdateObservation first = Parse("NEW", 10);
        BinanceAlgoUpdateObservation duplicate = Parse("NEW", 10);
        BinanceAlgoUpdateObservation transitioned = Parse("TRIGGERED", 11);

        Assert.True(BinanceAlgoProtocolFixtureParser.IsDuplicate(first, duplicate));
        Assert.False(BinanceAlgoProtocolFixtureParser.IsDuplicate(first, transitioned));
        Assert.Equal(7001, first.OpaqueOrder.GetProperty("algoId").GetInt64());
        Assert.Equal(7001, transitioned.OpaqueOrder.GetProperty("algoId").GetInt64());
        Assert.NotEqual(first.EventIdentity, transitioned.EventIdentity);
        Assert.Equal("TRIGGERED", transitioned.OpaqueOrder.GetProperty("status").GetString());
    }

    [Fact]
    public void EqualAndLateExchangeTimesKeepStableSessionOrderSeparate()
    {
        BinanceAlgoUpdateObservation equalTimeFirst = Parse("NEW", 20,
            eventTime: 1700000000200, transactionTime: 1700000000199);
        BinanceAlgoUpdateObservation equalTimeSecond = Parse("WORKING", 21,
            eventTime: 1700000000200, transactionTime: 1700000000199);
        BinanceAlgoUpdateObservation late = Parse("CANCELED", 22,
            eventTime: 1700000000100, transactionTime: 1700000000099);

        Assert.Equal(equalTimeFirst.EventTimeMilliseconds, equalTimeSecond.EventTimeMilliseconds);
        Assert.True(equalTimeFirst.StableSequence < equalTimeSecond.StableSequence);
        Assert.True(late.EventTimeMilliseconds < equalTimeFirst.EventTimeMilliseconds);
        Assert.Equal(22, late.StableSequence);
    }

    [Fact]
    public void UnknownAdditionalFieldsAndStatusValuesRemainOpaque()
    {
        BinanceAlgoUpdateObservation observation = BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.UnknownStatusUpdate,
            new BinanceAlgoObservationMetadata("session-a", 30,
                new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000300)),
                new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000301))));

        Assert.Equal("FUTURE_STATUS", observation.OpaqueOrder.GetProperty("status").GetString());
        Assert.True(observation.OpaqueOrder.GetProperty("futureField").GetProperty("opaque").GetBoolean());
        Assert.Equal(JsonValueKind.Object, observation.OpaqueOrder.ValueKind);
    }

    [Fact]
    public void RejectsMissingOrWronglyTypedConfirmedEnvelopeFields()
    {
        Assert.Throws<JsonException>(() => BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.MissingTransactionTime,
            Metadata(40)));
        Assert.Throws<JsonException>(() => BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.WrongEventTimeType,
            Metadata(41)));
        Assert.Throws<JsonException>(() => BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.MissingOpaqueOrder,
            Metadata(42)));
    }

    private static BinanceAlgoUpdateObservation Parse(string status, long sequence,
        long eventTime = 1700000000100, long transactionTime = 1700000000099)
    {
        return BinanceAlgoProtocolFixtureParser.Parse(
            BinanceAlgoProtocolFixtures.AlgoUpdate(7001, status, eventTime, transactionTime),
            new BinanceAlgoObservationMetadata("session-a", sequence,
                new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000 + sequence)),
                new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000001 + sequence))));
    }

    private static BinanceAlgoObservationMetadata Metadata(long sequence)
    {
        return new BinanceAlgoObservationMetadata("session-a", sequence,
            new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000)),
            new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000001)));
    }
}
