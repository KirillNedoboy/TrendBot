namespace TradingBot.Core;

/// <summary>
/// Pure independent risk evaluation boundary. It returns passive evidence only;
/// it has no order, reservation, exchange, or activation behavior.
/// </summary>
public static class RiskEvaluationEngine
{
    public const string CurrentAlgorithmVersion = RiskEvaluationVersion.CurrentValue;

    public static RiskEvaluationResult Evaluate(RiskEvaluationInput input,
        RiskEvaluationConfiguration configuration, IRiskMathCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(calculator);

        List<string> allReasons = [];
        List<RiskCheckEvidence> checks = [];
        Decision? decisionEvidence = input.DecisionEvidence;

        bool decisionPassed = ValidateDecision(input, decisionEvidence, allReasons);
        bool killSwitchPassed = ValidateKillSwitch(input, allReasons);
        bool healthPassed = ValidateHealth(input, allReasons);
        bool configurationPassed = ValidateConfiguration(input, configuration, allReasons);

        RiskMathResult? mathResult = null;
        bool sizingPassed = false;
        if (killSwitchPassed && configurationPassed)
        {
            sizingPassed = TryCalculate(input, configuration, calculator, allReasons,
                out mathResult);
        }
        else
        {
            allReasons.Add(RiskReasonCodes.RiskMathUnavailable);
        }

        bool limitsPassed = ValidateLimits(input, configuration, mathResult, allReasons);
        bool protectionPassed = ValidateProtection(input, mathResult, decisionEvidence,
            allReasons);
        bool cooldownPassed = ValidateCooldown(input, allReasons);

        checks.Add(CreateCheck("decision", decisionPassed, allReasons,
            RiskReasonCodes.DecisionOutcomeNotAllow, RiskReasonCodes.DecisionProjectionMissing,
            RiskReasonCodes.DecisionIdentityMismatch, RiskReasonCodes.DecisionRecoveryRequired));
        checks.Add(CreateCheck("system", killSwitchPassed && healthPassed,
            allReasons, RiskReasonCodes.KillSwitchUnknown, RiskReasonCodes.KillSwitchEnabled,
            RiskReasonCodes.AccountStateUnknown, RiskReasonCodes.AccountStateUnhealthy,
            RiskReasonCodes.PortfolioStateUnknown, RiskReasonCodes.PortfolioStateUnhealthy,
            RiskReasonCodes.MarketStateUnknown, RiskReasonCodes.MarketStateUnhealthy,
            RiskReasonCodes.EvidenceStale, RiskReasonCodes.EvidenceFuture,
            RiskReasonCodes.EvidenceConflict, RiskReasonCodes.EvidenceCursorMismatch));
        checks.Add(CreateCheck("sizing", sizingPassed, allReasons,
            RiskReasonCodes.RiskMathVersionMismatch, RiskReasonCodes.RiskMathInputMismatch,
            RiskReasonCodes.RiskMathCalculationFailed, RiskReasonCodes.RiskMathOverflow,
            RiskReasonCodes.RiskMathUnavailable));
        checks.Add(CreateCheck("limits", limitsPassed, allReasons,
            RiskReasonCodes.LimitsMissing, RiskReasonCodes.PortfolioLimitsMissing,
            RiskReasonCodes.LimitCurrencyMismatch, RiskReasonCodes.PerTradeRiskExceeded,
            RiskReasonCodes.DailyRiskExceeded, RiskReasonCodes.PortfolioRiskExceeded,
            RiskReasonCodes.NotionalExceeded, RiskReasonCodes.MarginExceeded,
            RiskReasonCodes.PositionSlotsExceeded, RiskReasonCodes.DailyLossUnknown,
            RiskReasonCodes.DailyLossPeriodMismatch, RiskReasonCodes.CapacityUsageUnknown,
            RiskReasonCodes.CapacityArithmeticOverflow, RiskReasonCodes.CandidateMarginUnknown,
            RiskReasonCodes.CandidateSlotsUnknown));
        checks.Add(CreateCheck("protection", protectionPassed, allReasons,
            RiskReasonCodes.ProtectiveEvidenceMissing, RiskReasonCodes.ProtectiveEvidenceInvalid,
            RiskReasonCodes.ProtectiveEvidenceStale, RiskReasonCodes.ProtectiveEvidenceFuture,
            RiskReasonCodes.ProtectiveEvidenceConflict));
        checks.Add(CreateCheck("cooldown", cooldownPassed, allReasons,
            RiskReasonCodes.CooldownUnknown, RiskReasonCodes.CooldownActive,
            RiskReasonCodes.CooldownInstrumentMismatch, RiskReasonCodes.EvidenceStale,
            RiskReasonCodes.EvidenceFuture, RiskReasonCodes.EvidenceConflict,
            RiskReasonCodes.EvidenceCursorMismatch));

        bool approved = decisionPassed && killSwitchPassed && healthPassed
            && configurationPassed && sizingPassed && limitsPassed && protectionPassed
            && cooldownPassed;
        if (approved)
        {
            allReasons.Add(RiskReasonCodes.Approved);
        }

        ImmutableValueList<string> reasons = StableReasons(allReasons);
        if (reasons.Count == 0)
        {
            reasons = new ImmutableValueList<string>([RiskReasonCodes.InputInvalid]);
        }

        RiskDecision decision = new($"risk-{input.EvaluationId}",
            decisionEvidence?.DecisionId ?? input.EvaluationId,
            approved ? RiskDecisionOutcome.Approved : RiskDecisionOutcome.Blocked,
            reasons);
        return RiskEvaluationResult.Create(input, configuration, decision, mathResult, checks,
            reasons);
    }

    private static bool ValidateDecision(RiskEvaluationInput input, Decision? decision,
        List<string> reasons)
    {
        bool passed = true;
        if (input.DecisionProjection is null)
        {
            reasons.Add(RiskReasonCodes.DecisionProjectionMissing);
            passed = false;
        }

        if (decision is null)
        {
            reasons.Add(RiskReasonCodes.DecisionIdentityMismatch);
            passed = false;
        }
        else if (decision.Outcome != DecisionOutcome.Allow)
        {
            reasons.Add(RiskReasonCodes.DecisionOutcomeNotAllow);
            passed = false;
        }

        if (input.DecisionProjection is not null)
        {
            if (input.DecisionProjection.CausalCursor != input.Cursor)
            {
                reasons.Add(IsFuture(input.DecisionProjection.CausalCursor, input.Cursor)
                    ? RiskReasonCodes.EvidenceFuture
                    : RiskReasonCodes.DecisionIdentityMismatch);
                passed = false;
            }

            if (input.DecisionProjection.RecoveryRequired)
            {
                reasons.Add(RiskReasonCodes.DecisionRecoveryRequired);
                passed = false;
            }

            SetupLifecycleIdentity identity = input.DecisionProjection.LifecycleIdentity;
            if (identity.InstrumentId != input.RiskMathInput.InstrumentId
                || identity.Direction != input.RiskMathInput.Direction)
            {
                reasons.Add(RiskReasonCodes.DecisionIdentityMismatch);
                passed = false;
            }
        }

        return passed;
    }

    private static bool ValidateConfiguration(RiskEvaluationInput input,
        RiskEvaluationConfiguration configuration, List<string> reasons)
    {
        bool passed = true;
        if (!StringComparer.Ordinal.Equals(configuration.Version.Value,
                RiskEvaluationVersion.CurrentValue))
        {
            reasons.Add(RiskReasonCodes.ConfigurationInvalid);
            passed = false;
        }

        if (configuration.Limits is null)
        {
            reasons.Add(RiskReasonCodes.LimitsMissing);
            passed = false;
        }

        if (configuration.PortfolioLimits is null)
        {
            reasons.Add(RiskReasonCodes.PortfolioLimitsMissing);
            passed = false;
        }
        else if (configuration.PortfolioLimits.PortfolioId != input.PortfolioId)
        {
            reasons.Add(RiskReasonCodes.DecisionIdentityMismatch);
            passed = false;
        }

        string currency = input.RiskMathInput.Equity.Currency;
        if (configuration.Limits is { } limits)
        {
            foreach (Money? value in new[]
            {
                limits.MaxPerTradeRisk, limits.MaxDailyLoss, limits.MaxMargin
            })
            {
                if (value is not null && !StringComparer.Ordinal.Equals(value.Currency, currency))
                {
                    reasons.Add(RiskReasonCodes.LimitCurrencyMismatch);
                    passed = false;
                }
            }
        }

        if (configuration.PortfolioLimits is { } portfolioLimits
            && (!StringComparer.Ordinal.Equals(portfolioLimits.MaxRisk.Currency, currency)
                || !StringComparer.Ordinal.Equals(portfolioLimits.MaxNotionalExposure.Currency,
                    currency)))
        {
            reasons.Add(RiskReasonCodes.LimitCurrencyMismatch);
            passed = false;
        }

        return passed;
    }

    private static bool ValidateKillSwitch(RiskEvaluationInput input, List<string> reasons)
    {
        RiskKillSwitchEvidence? evidence = input.KillSwitch;
        if (evidence is null)
        {
            reasons.Add(RiskReasonCodes.KillSwitchUnknown);
            return false;
        }

        if (!ValidateCausal(evidence.Cursor, input.Cursor, reasons,
                RiskReasonCodes.EvidenceFuture, RiskReasonCodes.EvidenceCursorMismatch))
        {
            return false;
        }

        if (evidence.Freshness == RiskEvidenceFreshness.Stale)
        {
            reasons.Add(RiskReasonCodes.EvidenceStale);
            return false;
        }

        if (evidence.Freshness == RiskEvidenceFreshness.Conflicting)
        {
            reasons.Add(RiskReasonCodes.EvidenceConflict);
            return false;
        }

        if (evidence.Freshness != RiskEvidenceFreshness.Fresh)
        {
            reasons.Add(RiskReasonCodes.KillSwitchUnknown);
            return false;
        }

        return evidence.Status switch
        {
            RiskKillSwitchStatus.Clear => true,
            RiskKillSwitchStatus.Enabled => AddAndFalse(reasons,
                RiskReasonCodes.KillSwitchEnabled),
            _ => AddAndFalse(reasons, RiskReasonCodes.KillSwitchUnknown)
        };
    }

    private static bool ValidateHealth(RiskEvaluationInput input, List<string> reasons)
    {
        bool account = ValidateHealthEvidence(input.AccountHealth, input.Cursor,
            input.RiskMathInput.InstrumentId, input.PortfolioId,
            RiskReasonCodes.AccountStateUnknown, RiskReasonCodes.AccountStateUnhealthy,
            reasons);
        bool portfolio = ValidateHealthEvidence(input.PortfolioHealth, input.Cursor,
            input.RiskMathInput.InstrumentId, input.PortfolioId,
            RiskReasonCodes.PortfolioStateUnknown, RiskReasonCodes.PortfolioStateUnhealthy,
            reasons);
        bool market = ValidateHealthEvidence(input.MarketHealth, input.Cursor,
            input.RiskMathInput.InstrumentId, input.PortfolioId,
            RiskReasonCodes.MarketStateUnknown, RiskReasonCodes.MarketStateUnhealthy,
            reasons);
        return account && portfolio && market;
    }

    private static bool ValidateHealthEvidence(RiskHealthEvidence? evidence,
        CausalEventCursor cursor, InstrumentId instrument, PortfolioId portfolio,
        string unknownReason, string unhealthyReason, List<string> reasons)
    {
        if (evidence is null)
        {
            reasons.Add(unknownReason);
            return false;
        }

        bool passed = ValidateCausal(evidence.Cursor, cursor, reasons,
            RiskReasonCodes.EvidenceFuture, RiskReasonCodes.EvidenceCursorMismatch);
        if (evidence.InstrumentId is not null && evidence.InstrumentId != instrument)
        {
            reasons.Add(RiskReasonCodes.EvidenceCursorMismatch);
            passed = false;
        }

        if (evidence.PortfolioId is not null && evidence.PortfolioId != portfolio)
        {
            reasons.Add(RiskReasonCodes.EvidenceCursorMismatch);
            passed = false;
        }

        if (evidence.Freshness == RiskEvidenceFreshness.Stale)
        {
            reasons.Add(RiskReasonCodes.EvidenceStale);
            passed = false;
        }
        else if (evidence.Freshness == RiskEvidenceFreshness.Conflicting)
        {
            reasons.Add(RiskReasonCodes.EvidenceConflict);
            passed = false;
        }
        else if (evidence.Freshness != RiskEvidenceFreshness.Fresh)
        {
            reasons.Add(unknownReason);
            passed = false;
        }

        switch (evidence.State)
        {
            case RiskHealthState.Healthy:
                break;
            case RiskHealthState.Unhealthy:
                reasons.Add(unhealthyReason);
                passed = false;
                break;
            case RiskHealthState.Conflicting:
                reasons.Add(RiskReasonCodes.EvidenceConflict);
                passed = false;
                break;
            default:
                reasons.Add(unknownReason);
                passed = false;
                break;
        }

        return passed;
    }

    private static bool TryCalculate(RiskEvaluationInput input,
        RiskEvaluationConfiguration configuration, IRiskMathCalculator calculator,
        List<string> reasons, out RiskMathResult? result)
    {
        result = null;
        RiskMathVersion calculatorVersion;
        try
        {
            calculatorVersion = calculator.Version;
        }
        catch (Exception)
        {
            reasons.Add(RiskReasonCodes.RiskMathCalculationFailed);
            return false;
        }

        if (calculatorVersion != configuration.RiskMathVersion)
        {
            reasons.Add(RiskReasonCodes.RiskMathVersionMismatch);
            return false;
        }

        try
        {
            result = calculator.Calculate(input.RiskMathInput);
        }
        catch (OverflowException)
        {
            reasons.Add(RiskReasonCodes.RiskMathOverflow);
            return false;
        }
        catch (ArithmeticException)
        {
            reasons.Add(RiskReasonCodes.RiskMathOverflow);
            return false;
        }
        catch (Exception)
        {
            reasons.Add(RiskReasonCodes.RiskMathCalculationFailed);
            return false;
        }

        if (result.Version != configuration.RiskMathVersion)
        {
            reasons.Add(RiskReasonCodes.RiskMathVersionMismatch);
            result = null;
            return false;
        }

        if (result.Input != input.RiskMathInput)
        {
            reasons.Add(RiskReasonCodes.RiskMathInputMismatch);
            result = null;
            return false;
        }

        return true;
    }

    private static bool ValidateLimits(RiskEvaluationInput input,
        RiskEvaluationConfiguration configuration, RiskMathResult? math,
        List<string> reasons)
    {
        bool passed = true;
        if (math is null)
        {
            reasons.Add(RiskReasonCodes.RiskMathUnavailable);
            return false;
        }

        RiskLimitConfiguration? limits = configuration.Limits;
        PortfolioLimits? portfolioLimits = configuration.PortfolioLimits;
        if (limits is null)
        {
            reasons.Add(RiskReasonCodes.LimitsMissing);
            passed = false;
        }

        if (portfolioLimits is null)
        {
            reasons.Add(RiskReasonCodes.PortfolioLimitsMissing);
            passed = false;
        }

        RiskPortfolioUsage? usage = input.PortfolioUsage;
        if (usage is null)
        {
            reasons.Add(RiskReasonCodes.CapacityUsageUnknown);
            passed = false;
        }
        else
        {
            if (usage.PortfolioId != input.PortfolioId || usage.Cursor != input.Cursor)
            {
                reasons.Add(RiskReasonCodes.EvidenceCursorMismatch);
                passed = false;
            }

            string currency = input.RiskMathInput.Equity.Currency;
            if (!HasCurrency(usage.Risk, currency)
                || !HasCurrency(usage.NotionalExposure, currency)
                || !HasCurrency(usage.Margin, currency))
            {
                reasons.Add(RiskReasonCodes.LimitCurrencyMismatch);
                passed = false;
            }
        }

        if (limits is null || portfolioLimits is null || usage is null)
        {
            return false;
        }

        if (limits.MaxPerTradeRisk is null || limits.MaxDailyLoss is null
            || limits.MaxMargin is null)
        {
            reasons.Add(RiskReasonCodes.LimitsMissing);
            passed = false;
        }

        if (input.AccumulatedDailyLoss is null)
        {
            reasons.Add(RiskReasonCodes.DailyLossUnknown);
            passed = false;
        }
        else if (!HasCurrency(input.AccumulatedDailyLoss, math.RiskBudget.Currency))
        {
            reasons.Add(RiskReasonCodes.LimitCurrencyMismatch);
            passed = false;
        }

        if (limits.DailyLossPeriodId is null || input.AccumulatedLossPeriodId is null)
        {
            reasons.Add(RiskReasonCodes.DailyLossUnknown);
            passed = false;
        }
        else if (!StringComparer.Ordinal.Equals(limits.DailyLossPeriodId,
                     input.AccumulatedLossPeriodId))
        {
            reasons.Add(RiskReasonCodes.DailyLossPeriodMismatch);
            passed = false;
        }

        if (input.CandidateMargin is null)
        {
            reasons.Add(RiskReasonCodes.CandidateMarginUnknown);
            passed = false;
        }
        else if (!HasCurrency(input.CandidateMargin, math.RiskBudget.Currency))
        {
            reasons.Add(RiskReasonCodes.LimitCurrencyMismatch);
            passed = false;
        }

        if (input.CandidatePositionSlots is null)
        {
            reasons.Add(RiskReasonCodes.CandidateSlotsUnknown);
            passed = false;
        }

        if (limits.MaxPerTradeRisk is { } perTrade
            && math.RiskBudget.Amount > perTrade.Amount)
        {
            reasons.Add(RiskReasonCodes.PerTradeRiskExceeded);
            passed = false;
        }

        try
        {
            if (limits.MaxDailyLoss is { } daily && input.AccumulatedDailyLoss is { } loss
                && checked(loss.Amount + math.RiskBudget.Amount) > daily.Amount)
            {
                reasons.Add(RiskReasonCodes.DailyRiskExceeded);
                passed = false;
            }

            if (portfolioLimits.MaxRisk.Amount < checked(usage.Risk.Amount
                + math.RiskBudget.Amount))
            {
                reasons.Add(RiskReasonCodes.PortfolioRiskExceeded);
                passed = false;
            }

            if (portfolioLimits.MaxNotionalExposure.Amount < checked(
                    usage.NotionalExposure.Amount + math.NotionalExposure.Amount))
            {
                reasons.Add(RiskReasonCodes.NotionalExceeded);
                passed = false;
            }

            if (limits.MaxMargin is { } maxMargin && input.CandidateMargin is { } margin
                && maxMargin.Amount < checked(usage.Margin.Amount + margin.Amount))
            {
                reasons.Add(RiskReasonCodes.MarginExceeded);
                passed = false;
            }

            if (input.CandidatePositionSlots is { } candidateSlots
                && portfolioLimits.MaxPositionSlots < checked(usage.PositionSlots
                    + candidateSlots))
            {
                reasons.Add(RiskReasonCodes.PositionSlotsExceeded);
                passed = false;
            }
        }
        catch (OverflowException)
        {
            reasons.Add(RiskReasonCodes.CapacityArithmeticOverflow);
            passed = false;
        }

        return passed;
    }

    private static bool ValidateProtection(RiskEvaluationInput input, RiskMathResult? math,
        Decision? decision, List<string> reasons)
    {
        RiskProtectionEvidence? protection = input.Protection;
        if (protection is null)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceMissing);
            return false;
        }

        bool passed = true;
        if (math is null)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
            return false;
        }

        if (!ValidateCausal(protection.Cursor, input.Cursor, reasons,
                RiskReasonCodes.ProtectiveEvidenceFuture,
                RiskReasonCodes.ProtectiveEvidenceInvalid))
        {
            passed = false;
        }

        if (protection.Freshness == RiskEvidenceFreshness.Stale)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceStale);
            passed = false;
        }
        else if (protection.Freshness == RiskEvidenceFreshness.Conflicting)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceConflict);
            passed = false;
        }
        else if (protection.Freshness != RiskEvidenceFreshness.Fresh)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
            passed = false;
        }

        if (protection.InstrumentId != math.Input.InstrumentId
            || protection.Direction != math.Input.Direction
            || decision is null
            || !StringComparer.Ordinal.Equals(protection.SetupId, decision.SetupId))
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
            passed = false;
        }

        RiskProtectionLevelEvidence[] levels =
        [protection.ProtectiveStop, protection.TakeProfit1, protection.TakeProfit2];
        RiskProtectionLevelKind[] kinds =
        [RiskProtectionLevelKind.ProtectiveStop, RiskProtectionLevelKind.TakeProfit1,
            RiskProtectionLevelKind.TakeProfit2];
        foreach ((RiskProtectionLevelEvidence level, RiskProtectionLevelKind kind) in
            levels.Zip(kinds))
        {
            if (level.Kind != kind || level.InstrumentId != protection.InstrumentId
                || level.Direction != protection.Direction
                || !StringComparer.Ordinal.Equals(level.SetupId, protection.SetupId)
                || !StringComparer.Ordinal.Equals(level.MethodId, protection.MethodId)
                || !StringComparer.Ordinal.Equals(level.SourceId, protection.SourceId)
                || !StringComparer.Ordinal.Equals(level.EvidenceVersion,
                    protection.EvidenceVersion)
                || level.Freshness != protection.Freshness || level.Cursor != protection.Cursor)
            {
                reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
                passed = false;
            }
        }

        if (protection.ProtectiveStop.Price != math.Input.ProtectiveStopPrice)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
            passed = false;
        }

        if (math.Input.Direction == Direction.Long)
        {
            if (protection.ProtectiveStop.Price.Value >= math.Input.EntryPrice.Value
                || protection.TakeProfit1.Price.Value <= math.Input.EntryPrice.Value
                || protection.TakeProfit2.Price.Value <= math.Input.EntryPrice.Value)
            {
                reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
                passed = false;
            }
        }
        else if (protection.ProtectiveStop.Price.Value <= math.Input.EntryPrice.Value
            || protection.TakeProfit1.Price.Value >= math.Input.EntryPrice.Value
            || protection.TakeProfit2.Price.Value >= math.Input.EntryPrice.Value)
        {
            reasons.Add(RiskReasonCodes.ProtectiveEvidenceInvalid);
            passed = false;
        }

        return passed;
    }

    private static bool ValidateCooldown(RiskEvaluationInput input, List<string> reasons)
    {
        RiskCooldownState? cooldown = input.Cooldown;
        if (cooldown is null)
        {
            reasons.Add(RiskReasonCodes.CooldownUnknown);
            return false;
        }

        bool passed = ValidateCausal(cooldown.Cursor, input.Cursor, reasons,
            RiskReasonCodes.EvidenceFuture, RiskReasonCodes.EvidenceCursorMismatch);
        if (cooldown.InstrumentId != input.RiskMathInput.InstrumentId)
        {
            reasons.Add(RiskReasonCodes.CooldownInstrumentMismatch);
            passed = false;
        }

        if (cooldown.Freshness == RiskEvidenceFreshness.Stale)
        {
            reasons.Add(RiskReasonCodes.EvidenceStale);
            passed = false;
        }
        else if (cooldown.Freshness == RiskEvidenceFreshness.Conflicting)
        {
            reasons.Add(RiskReasonCodes.EvidenceConflict);
            passed = false;
        }
        else if (cooldown.Freshness != RiskEvidenceFreshness.Fresh)
        {
            reasons.Add(RiskReasonCodes.CooldownUnknown);
            passed = false;
        }

        if (cooldown.Status == RiskCooldownStatus.Unknown)
        {
            reasons.Add(RiskReasonCodes.CooldownUnknown);
            passed = false;
        }
        else if (cooldown.Status == RiskCooldownStatus.ActiveUntil
            && cooldown.ActiveUntil!.Value > input.Cursor.AsOf.Value)
        {
            reasons.Add(RiskReasonCodes.CooldownActive);
            passed = false;
        }

        return passed;
    }

    private static bool ValidateCausal(CausalEventCursor evidence,
        CausalEventCursor expected, List<string> reasons, string futureReason,
        string mismatchReason)
    {
        if (IsFuture(evidence, expected))
        {
            reasons.Add(futureReason);
            return false;
        }

        if (evidence != expected)
        {
            reasons.Add(mismatchReason);
            return false;
        }

        return true;
    }

    private static bool IsFuture(CausalEventCursor candidate, CausalEventCursor expected)
    {
        int time = candidate.AsOf.Value.CompareTo(expected.AsOf.Value);
        return time > 0 || (time == 0
            && candidate.MaxStableSequence > expected.MaxStableSequence);
    }

    private static bool HasCurrency(Money value, string currency) =>
        StringComparer.Ordinal.Equals(value.Currency, currency);

    private static bool AddAndFalse(List<string> reasons, string reason)
    {
        reasons.Add(reason);
        return false;
    }

    private static RiskCheckEvidence CreateCheck(string checkId, bool passed,
        IEnumerable<string> allReasons, params string[] relevantReasons)
    {
        HashSet<string> relevant = new(relevantReasons, StringComparer.Ordinal);
        List<string> reasons = [.. allReasons.Where(relevant.Contains)];
        if (reasons.Count == 0)
        {
            reasons.Add(passed ? $"risk-{checkId}-passed" : RiskReasonCodes.InputInvalid);
        }

        return new RiskCheckEvidence(checkId, passed, StableReasons(reasons));
    }

    private static ImmutableValueList<string> StableReasons(IEnumerable<string> reasons) =>
        new(reasons.Where(static reason => !String.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
}
