using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>An opaque, ordinally compared instrument identifier.</summary>
public sealed record InstrumentId
{
    public string Value { get; }

    [JsonConstructor]
    public InstrumentId(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>A positive exact decimal price.</summary>
public sealed record Price
{
    public decimal Value { get; }

    [JsonConstructor]
    public Price(decimal value)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A price must be greater than zero.");
        }

        Value = value;
    }
}

/// <summary>A non-negative exact decimal quantity.</summary>
public sealed record Quantity
{
    public decimal Value { get; }

    [JsonConstructor]
    public Quantity(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A quantity cannot be negative.");
        }

        Value = value;
    }
}

/// <summary>An exact signed decimal amount denominated in a named currency.</summary>
public sealed record Money
{
    public decimal Amount { get; }

    public string Currency { get; }

    [JsonConstructor]
    public Money(decimal amount, string currency)
    {
        ContractGuard.RequireText(currency, nameof(currency));
        Amount = amount;
        Currency = currency;
    }
}

/// <summary>A timestamp value normalized to UTC.</summary>
public sealed record UtcTimestamp
{
    public DateTimeOffset Value { get; }

    [JsonConstructor]
    public UtcTimestamp(DateTimeOffset value)
    {
        Value = value.ToUniversalTime();
    }
}

/// <summary>An immutable, structurally compared snapshot of values.</summary>
public sealed class ImmutableValueList<T> : IReadOnlyList<T>, IEquatable<ImmutableValueList<T>>
{
    private readonly T[] _values;

    public int Count => _values.Length;

    public T this[int index] => _values[index];

    public ImmutableValueList(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ImmutableValueList<T>? other)
    {
        return other is not null && _values.SequenceEqual(other._values);
    }

    public override bool Equals(object? obj) => obj is ImmutableValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (T value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

internal static class ContractGuard
{
    public static void RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }

    public static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The enum value is not defined.");
        }
    }

    public static ImmutableValueList<string> RequireReasons(IEnumerable<string> reasons, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reasons, parameterName);
        ImmutableValueList<string> snapshot = new(reasons);
        if (snapshot.Count == 0 || snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty reason is required.", parameterName);
        }

        return snapshot;
    }
}
