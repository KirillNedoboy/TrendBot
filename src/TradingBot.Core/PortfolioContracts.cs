using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies a caller-owned portfolio.</summary>
public sealed record PortfolioId
{
    /// <summary>Gets the non-empty, ordinally compared portfolio identifier.</summary>
    public string Value { get; }

    /// <summary>Creates a portfolio identifier without applying a trading default.</summary>
    [JsonConstructor]
    public PortfolioId(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Captures the immutable, caller-supplied capacity limits for one portfolio.
/// </summary>
public sealed record PortfolioLimits
{
    /// <summary>Gets the portfolio to which these limits belong.</summary>
    public PortfolioId PortfolioId { get; }

    /// <summary>Gets the maximum reserved monetary risk.</summary>
    public Money MaxRisk { get; }

    /// <summary>Gets the maximum reserved notional exposure.</summary>
    public Money MaxNotionalExposure { get; }

    /// <summary>Gets the maximum number of reserved position slots.</summary>
    public int MaxPositionSlots { get; }

    /// <summary>Creates explicit, non-negative portfolio capacity limits.</summary>
    [JsonConstructor]
    public PortfolioLimits(PortfolioId portfolioId, Money maxRisk, Money maxNotionalExposure,
        int maxPositionSlots)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(maxRisk);
        ArgumentNullException.ThrowIfNull(maxNotionalExposure);
        RequireNonNegative(maxRisk, nameof(maxRisk));
        RequireNonNegative(maxNotionalExposure, nameof(maxNotionalExposure));
        RequireMatchingCurrency(maxRisk.Currency, maxNotionalExposure.Currency,
            nameof(maxNotionalExposure));
        if (maxPositionSlots < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPositionSlots), maxPositionSlots,
                "The maximum position slots cannot be negative.");
        }

        PortfolioId = portfolioId;
        MaxRisk = maxRisk;
        MaxNotionalExposure = maxNotionalExposure;
        MaxPositionSlots = maxPositionSlots;
    }

    private static void RequireNonNegative(Money value, string parameterName)
    {
        if (value.Amount < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Amount,
                "A portfolio capacity cannot be negative.");
        }
    }

    private static void RequireMatchingCurrency(string expected, string actual,
        string parameterName)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual))
        {
            throw new ArgumentException("Portfolio monetary values must use the same currency.",
                parameterName);
        }
    }
}

/// <summary>Captures one immutable caller-supplied reservation request.</summary>
public sealed record PortfolioReservation
{
    /// <summary>Gets the caller-supplied reservation identity.</summary>
    public string ReservationId { get; }

    /// <summary>Gets the portfolio whose capacity is reserved.</summary>
    public PortfolioId PortfolioId { get; }

    /// <summary>Gets the order intent associated with the reservation.</summary>
    public string IntentId { get; }

    /// <summary>Gets the reserved monetary risk.</summary>
    public Money Risk { get; }

    /// <summary>Gets the reserved notional exposure.</summary>
    public Money NotionalExposure { get; }

    /// <summary>Gets the reserved number of position slots.</summary>
    public int PositionSlots { get; }

    /// <summary>Creates an explicit, non-negative reservation without defaults.</summary>
    [JsonConstructor]
    public PortfolioReservation(string reservationId, PortfolioId portfolioId, string intentId,
        Money risk, Money notionalExposure, int positionSlots)
    {
        ContractGuard.RequireText(reservationId, nameof(reservationId));
        ArgumentNullException.ThrowIfNull(portfolioId);
        ContractGuard.RequireText(intentId, nameof(intentId));
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(notionalExposure);
        RequireNonNegative(risk, nameof(risk));
        RequireNonNegative(notionalExposure, nameof(notionalExposure));
        if (!StringComparer.Ordinal.Equals(risk.Currency, notionalExposure.Currency))
        {
            throw new ArgumentException("Reservation monetary values must use the same currency.",
                nameof(notionalExposure));
        }

        if (positionSlots < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionSlots), positionSlots,
                "Reserved position slots cannot be negative.");
        }

        ReservationId = reservationId;
        PortfolioId = portfolioId;
        IntentId = intentId;
        Risk = risk;
        NotionalExposure = notionalExposure;
        PositionSlots = positionSlots;
    }

    private static void RequireNonNegative(Money value, string parameterName)
    {
        if (value.Amount < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Amount,
                "A reservation capacity cannot be negative.");
        }
    }
}

/// <summary>Describes the outcome of one idempotent reservation attempt.</summary>
public enum PortfolioReservationResult
{
    Reserved,
    AlreadyReserved,
    InsufficientCapacity
}
