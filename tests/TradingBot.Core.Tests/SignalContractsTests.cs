using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalContractsTests
{
    private static readonly UtcTimestamp SetupOpen = At("2026-11-09T00:00:00Z");
    private static readonly UtcTimestamp SetupClose = At("2026-11-09T00:15:00Z");
    private static readonly UtcTimestamp ContextClose = At("2026-11-09T00:00:00Z");

    [Fact]
    public void SignalIdentitySurvivesEvidenceChangesWhileRecordHashChanges()
    {
        SignalIdentity identity = Identity();
        SignalEvidenceRecord firstEvidence = Evidence(identity, "quality-a");
        SignalEvidenceRecord changedEvidence = Evidence(identity, "quality-b");
        SignalRecord first = SignalRecord.Create(identity, firstEvidence,
            SignalAssessmentState.ConfirmedOnClosedBar, BarInterval(), SetupClose,
            ReferencePrice());
        SignalRecord changed = SignalRecord.Create(identity, changedEvidence,
            SignalAssessmentState.ConfirmedOnClosedBar, BarInterval(), SetupClose,
            ReferencePrice());

        Assert.Equal(identity.SignalId, first.Identity.SignalId);
        Assert.Equal(first.Identity.SignalId, changed.Identity.SignalId);
        Assert.NotEqual(firstEvidence.RecordHash, changedEvidence.RecordHash);
        Assert.NotEqual(first.RecordHash, changed.RecordHash);
        Assert.NotEqual(first.Identity.SignalId, first.RecordHash);
    }

    [Fact]
    public void ConfirmedSignalRequiresCompleteEvidence()
    {
        SignalIdentity identity = Identity();
        SignalEvidenceRecord unknownEvidence = Evidence(identity,
            "required-input-unknown", SignalEvidenceState.Unknown);

        Assert.Throws<ArgumentException>(() => SignalRecord.Create(identity,
            unknownEvidence, SignalAssessmentState.ConfirmedOnClosedBar,
            BarInterval(), SetupClose, ReferencePrice()));
        SignalRecord researchRecord = SignalRecord.Create(identity, unknownEvidence,
            SignalAssessmentState.Research, BarInterval(), SetupClose, ReferencePrice());
        Assert.Equal(SignalAssessmentState.Research, researchRecord.AssessmentState);
    }

    [Fact]
    public void SignalContractRejectsMissingFieldsTamperedHashesAndUnknownSchema()
    {
        SignalRecord signal = CreateSignal();
        JsonObject missingField = JsonNode.Parse(CoreJsonSerializer.Serialize(signal))!
            .AsObject();
        missingField.Remove("decisionAt");
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalRecord>(missingField.ToJsonString()));
        JsonObject missingProbability = JsonNode.Parse(
            CoreJsonSerializer.Serialize(signal))!.AsObject();
        missingProbability.Remove("probability");
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<SignalRecord>(
            missingProbability.ToJsonString()));

        JsonObject unsupportedSchema = JsonNode.Parse(
            CoreJsonSerializer.Serialize(signal))!.AsObject();
        unsupportedSchema["formatVersion"] = SignalRecord.CurrentFormatVersion + 1;
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<SignalRecord>(
            unsupportedSchema.ToJsonString()));

        JsonObject tamperedHash = JsonNode.Parse(CoreJsonSerializer.Serialize(signal))!
            .AsObject();
        tamperedHash["recordHash"] = new string('0', 64);
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalRecord>(tamperedHash.ToJsonString()));

        JsonObject forbiddenField = JsonNode.Parse(CoreJsonSerializer.Serialize(signal))!
            .AsObject();
        forbiddenField["orderAuthorized"] = true;
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalRecord>(forbiddenField.ToJsonString()));

        JsonObject validationState = JsonNode.Parse(
            CoreJsonSerializer.Serialize(signal))!.AsObject();
        validationState["assessmentState"] = "Validated";
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<SignalRecord>(
            validationState.ToJsonString()));
    }

    [Fact]
    public void ProvenanceRejectsInputBeyondItsCausalCutAndIncompatibleHigherCut()
    {
        SignalInputReference lateInput = Input("setup-event", SignalInputRole.Setup,
            SetupClose, SetupClose, 21);
        Assert.Throws<ArgumentException>(() =>
            Provenance(setupInput: lateInput));

        CausalEventCursor afterDecision = new(At("2026-11-09T00:16:00Z"), 21);
        Assert.Throws<ArgumentException>(() => Provenance(higherTimeframeCut: afterDecision));
        Assert.Throws<ArgumentException>(() => new SignalInputReference(
            "binance-public", "native-kline-v1", "context-event",
            new string('c', 64), SignalInputRole.HigherTimeframe, "15m",
            ContextClose, ContextClose, 10));

        SignalIdentity identity = Identity();
        SignalEvidenceRecord evidence = Evidence(identity, "quality-a");
        SignalIdentity otherIdentity = Identity("different-setup-event");
        Assert.Throws<ArgumentException>(() => SignalRecord.Create(otherIdentity,
            evidence, SignalAssessmentState.Research, BarInterval(), SetupClose,
            ReferencePrice()));
    }

    [Fact]
    public void SignalEvidenceDeliveryAndOutcomeRoundTripAsSeparateRecords()
    {
        SignalIdentity identity = Identity();
        SignalEvidenceRecord evidence = Evidence(identity, "quality-a");
        SignalRecord signal = SignalRecord.Create(identity, evidence,
            SignalAssessmentState.ConfirmedOnClosedBar, BarInterval(), SetupClose,
            ReferencePrice());
        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(signal,
            "attempt-1", SignalDeliveryChannel.Telegram,
            SignalDeliveryStatus.Acknowledged, At("2026-11-09T00:16:00Z"),
            "external-receipt-1");
        SignalOutcomeRecord outcome = SignalOutcomeRecord.Create(signal, evidence,
            "outcome-method-v1", SignalOutcomeKind.ReferenceLevelReached,
            At("2026-11-09T01:00:00Z"), new Price(100m), []);

        SignalIdentity restoredIdentity = CoreJsonSerializer.Deserialize<SignalIdentity>(
            CoreJsonSerializer.Serialize(identity));
        SignalEvidenceRecord restoredEvidence = CoreJsonSerializer.Deserialize<SignalEvidenceRecord>(
            CoreJsonSerializer.Serialize(evidence));
        SignalRecord restoredSignal = CoreJsonSerializer.Deserialize<SignalRecord>(
            CoreJsonSerializer.Serialize(signal));
        SignalDeliveryReceipt restoredReceipt =
            CoreJsonSerializer.Deserialize<SignalDeliveryReceipt>(
                CoreJsonSerializer.Serialize(receipt));
        SignalOutcomeRecord restoredOutcome =
            CoreJsonSerializer.Deserialize<SignalOutcomeRecord>(
                CoreJsonSerializer.Serialize(outcome));

        Assert.Equal(identity, restoredIdentity);
        Assert.Equal(evidence, restoredEvidence);
        Assert.Equal(signal, restoredSignal);
        Assert.Equal(receipt, restoredReceipt);
        Assert.Equal(outcome, restoredOutcome);
        Assert.Equal(signal.Identity.SignalId, receipt.SignalId);
        Assert.Equal(signal.RecordHash, receipt.SignalRecordHash);
        Assert.Equal(signal.Identity.SignalId, outcome.SignalId);
        Assert.Equal(signal.RecordHash, outcome.SignalRecordHash);
        Assert.NotEqual(signal.RecordHash, receipt.ReceiptId);
        Assert.NotEqual(signal.RecordHash, outcome.OutcomeId);
        Assert.Null(signal.Probability);
        Assert.Null(signal.CalibrationVersion);
        receipt.ValidateFor(signal);
        outcome.ValidateFor(signal, evidence);
    }

    [Fact]
    public void DeliveryAndOutcomeRejectIncompatibleSignalHashesAndUnknownReasons()
    {
        SignalIdentity identity = Identity();
        SignalEvidenceRecord evidence = Evidence(identity, "quality-a");
        SignalRecord signal = SignalRecord.Create(identity, evidence,
            SignalAssessmentState.ConfirmedOnClosedBar, BarInterval(), SetupClose,
            ReferencePrice());
        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(signal,
            "attempt-2", SignalDeliveryChannel.LocalJournal,
            SignalDeliveryStatus.Attempted, At("2026-11-09T00:16:00Z"));
        SignalEvidenceRecord changedEvidence = Evidence(signal.Identity,
            "different-evidence");
        SignalRecord changedSignal = SignalRecord.Create(signal.Identity,
            changedEvidence, SignalAssessmentState.ConfirmedOnClosedBar,
            BarInterval(), SetupClose, ReferencePrice());
        Assert.Throws<ArgumentException>(() => receipt.ValidateFor(changedSignal));
        Assert.Throws<ArgumentException>(() => SignalDeliveryReceipt.Create(signal,
            "early-attempt", SignalDeliveryChannel.LocalJournal,
            SignalDeliveryStatus.Attempted, At("2026-11-09T00:14:00Z")));
        Assert.Throws<ArgumentException>(() => SignalOutcomeRecord.Create(signal,
            evidence, "outcome-method-v1", SignalOutcomeKind.Expired,
            At("2026-11-09T00:14:00Z"), null, []));
        SignalOutcomeRecord outcome = SignalOutcomeRecord.Create(signal, evidence,
            "outcome-method-v1", SignalOutcomeKind.Unknown,
            At("2026-11-09T01:00:00Z"), null, ["outcome-unavailable"]);
        Assert.Throws<ArgumentException>(() =>
            outcome.ValidateFor(changedSignal, changedEvidence));

        JsonObject alteredReceipt = JsonNode.Parse(CoreJsonSerializer.Serialize(receipt))!
            .AsObject();
        alteredReceipt["signalRecordHash"] = new string('b', 64);
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalDeliveryReceipt>(alteredReceipt.ToJsonString()));

        Assert.Throws<ArgumentException>(() => SignalOutcomeRecord.Create(signal,
            Evidence(identity, "different-evidence"), "outcome-method-v1",
            SignalOutcomeKind.Unknown, At("2026-11-09T01:00:00Z"), null,
            ["outcome-unavailable"]));
        Assert.Throws<ArgumentException>(() => SignalOutcomeRecord.Create(signal,
            evidence, "outcome-method-v1", SignalOutcomeKind.Unknown,
            At("2026-11-09T01:00:00Z"), null, []));

        JsonObject alteredOutcome = JsonNode.Parse(CoreJsonSerializer.Serialize(outcome))!
            .AsObject();
        alteredOutcome["outcomeId"] = new string('0', 64);
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SignalOutcomeRecord>(alteredOutcome.ToJsonString()));
    }

    private static SignalRecord CreateSignal()
    {
        SignalIdentity identity = Identity();
        return SignalRecord.Create(identity, Evidence(identity, "quality-a"),
            SignalAssessmentState.ConfirmedOnClosedBar, BarInterval(), SetupClose,
            ReferencePrice());
    }

    private static SignalIdentity Identity(string setupEventId = "setup-event") =>
        SignalIdentity.Create(new InstrumentId("BTCUSDT"), "BINANCE_USDM", "15m",
            Direction.Long, SetupType.BoundaryRejection, "profile-v1",
            "boundary-rejection-v1", setupEventId);

    private static SignalEvidenceRecord Evidence(SignalIdentity identity,
        string qualityReason, SignalEvidenceState state = SignalEvidenceState.Complete) =>
        SignalEvidenceRecord.Create(identity, Provenance(identity.SetupEventId),
            state, [qualityReason]);

    private static SignalProvenance Provenance(string setupEventId = "setup-event",
        SignalInputReference? setupInput = null,
        CausalEventCursor? higherTimeframeCut = null) => SignalProvenance.Create(
        "profile-v1", new string('a', 64),
        new CausalEventCursor(SetupClose, 20),
        higherTimeframeCut ?? new CausalEventCursor(ContextClose, 10),
        [setupInput ?? Input(setupEventId, SignalInputRole.Setup,
                SetupClose, SetupClose, 20),
            Input("context-event", SignalInputRole.HigherTimeframe,
                ContextClose, ContextClose, 10)]);

    private static SignalInputReference Input(string eventId, SignalInputRole role,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime, long sequence) =>
        new("binance-public", "native-kline-v1", eventId, new string('c', 64),
            role, role == SignalInputRole.Setup ? "15m" : "1h",
            eventTime, availabilityTime, sequence);

    private static SignalBarInterval BarInterval() => new(SetupOpen, SetupClose);

    private static SignalReferencePrice ReferencePrice(string eventId = "setup-event") =>
        new("binance-public", "native-kline-v1", eventId, SetupClose, new Price(100m));

    private static UtcTimestamp At(string value) =>
        new(DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
}
