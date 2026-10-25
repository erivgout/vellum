using Vellum.Domain;
using Vellum.Infrastructure;

namespace Vellum.Modules.AdverseEvents;

/// <summary>A file ready to hand to the FDA, with the profile it goes out under.</summary>
public sealed record EmdrOutbound(EmdrSubmission Submission, byte[] Payload, EmdrProfile Profile);

/// <param name="Status">The submission status after sending: AwaitingUpload, Sent or Failed.</param>
public sealed record GatewaySendResult(string Status, DateTime? SentAt, string Message, string? Error = null);

/// <param name="Type">ack1 | ack2 | ack3.</param>
public sealed record GatewayAck(string Type, bool Accepted, DateTime ReceivedAt, string? Detail, string? FdaReference);

/// <summary>
/// The seam between Vellum and the FDA Electronic Submissions Gateway (AER-008, INT-902). Implementations: manual (ESG WebTrader)
/// and simulated (non-production only). A real AS2 sender plugs in here once the customer's ESG account and certificates exist.
/// </summary>
public interface IEmdrGateway
{
    string Key { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>Acknowledgements are entered by a person (the file was uploaded by hand).</summary>
    bool ManualAcknowledgements { get; }
    /// <summary>Null when the gateway can be used with this profile in this environment; otherwise why not.</summary>
    string? UnavailableReason(EmdrProfile profile, EnvironmentType environment);
    Task<GatewaySendResult> SendAsync(EmdrOutbound message, CancellationToken ct = default);
    /// <summary>Acknowledgements that have arrived since the last poll.</summary>
    Task<IReadOnlyList<GatewayAck>> PollAsync(EmdrSubmission submission, DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>
/// Manual submission through ESG WebTrader: the user downloads the generated file, uploads it to the FDA themselves, records the
/// upload, and then records ACK1, ACK2 and ACK3 as the FDA sends them.
/// </summary>
public sealed class ManualEsgGateway : IEmdrGateway
{
    public string Key => "manual";
    public string Name => "Manual (ESG WebTrader)";
    public string Description => "Download the eMDR file, upload it with FDA ESG WebTrader, then record the upload and each acknowledgement here.";
    public bool ManualAcknowledgements => true;
    public string? UnavailableReason(EmdrProfile profile, EnvironmentType environment) => null;

    public Task<GatewaySendResult> SendAsync(EmdrOutbound message, CancellationToken ct = default) =>
        Task.FromResult(new GatewaySendResult(EmdrStatus.AwaitingUpload, null,
            $"{message.Submission.FileName} is ready. Download it, upload it with ESG WebTrader, then record the upload."));

    public Task<IReadOnlyList<GatewayAck>> PollAsync(EmdrSubmission submission, DateTime nowUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GatewayAck>>([]);
}

/// <summary>
/// A stand-in for the FDA, for demonstration and testing. Never available in a production environment. It "receives" the file at once
/// and returns ACK1, ACK2 and ACK3 on a timer, delivered by the acknowledgement job. The profile chooses whether ACK3 accepts the
/// report, rejects it with load errors, or never arrives (to exercise the overdue alert).
/// </summary>
public sealed class SimulatedEmdrGateway : IEmdrGateway
{
    public static readonly TimeSpan Ack1After = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Ack2After = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Ack3After = TimeSpan.FromMinutes(3);

    public string Key => "simulated";
    public string Name => "Simulated (non-production)";
    public string Description => "Pretends to be the FDA: acknowledgements arrive about 1, 2 and 3 minutes after sending. Nothing leaves Vellum.";
    public bool ManualAcknowledgements => false;

    public string? UnavailableReason(EmdrProfile profile, EnvironmentType environment) =>
        environment == EnvironmentType.Production ? "The simulated gateway is never available in a production environment." : null;

    public Task<GatewaySendResult> SendAsync(EmdrOutbound message, CancellationToken ct = default) =>
        Task.FromResult(new GatewaySendResult(EmdrStatus.Sent, DateTime.UtcNow,
            "Sent to the simulated FDA gateway. ACK1, ACK2 and ACK3 arrive over the next few minutes."));

    public Task<IReadOnlyList<GatewayAck>> PollAsync(EmdrSubmission s, DateTime nowUtc, CancellationToken ct = default)
    {
        var list = new List<GatewayAck>();
        if (s.SentAt is not { } sent || !EmdrStatus.IsPending(s.Status)) return Task.FromResult<IReadOnlyList<GatewayAck>>(list);
        var reference = "SIM-" + (s.MessageId.Aggregate(2166136261u, (h, ch) => (h ^ ch) * 16777619u) % 1_000_000).ToString("D6");
        if (s.Ack1At == null && nowUtc >= sent + Ack1After)
            list.Add(new("ack1", true, sent + Ack1After, "MDN: the message was received and decrypted by the (simulated) FDA ESG.", null));
        if (s.Ack2At == null && nowUtc >= sent + Ack2After)
            list.Add(new("ack2", true, sent + Ack2After, "Center receipt: the (simulated) CDRH received the submission. This is the official receipt date.", reference));
        if (s.Ack3At == null && nowUtc >= sent + Ack3After)
            switch (s.SimulatedOutcome ?? "accept")
            {
                case "reject_ack3":
                    list.Add(new("ack3", false, sent + Ack3After,
                        "Load failed (simulated): business rule error in section D: the product code isn't valid for the device's classification. Correct the report and resubmit.", reference));
                    break;
                case "withhold_ack3":
                    break; // the FDA stays silent: the receipt-delay alert takes over (AER-010)
                default:
                    list.Add(new("ack3", true, sent + Ack3After, "Loaded successfully into the (simulated) FDA MAUDE database. No errors.", reference));
                    break;
            }
        return Task.FromResult<IReadOnlyList<GatewayAck>>(list);
    }
}

/// <summary>
/// The seam for a real AS2 sender. It needs the customer's FDA ESG account, its AS2 identifiers and the exchanged certificates
/// (see <see cref="EmdrProfileService.SigningCertificateForGateway"/>), so it isn't implemented in this release.
/// </summary>
public sealed class As2EmdrGateway : IEmdrGateway
{
    public string Key => "as2";
    public string Name => "AS2 (FDA ESG)";
    public string Description => EmdrGateways.As2NotAvailable;
    public bool ManualAcknowledgements => false;
    public string? UnavailableReason(EmdrProfile profile, EnvironmentType environment) => EmdrGateways.As2NotAvailable;
    public Task<GatewaySendResult> SendAsync(EmdrOutbound message, CancellationToken ct = default) =>
        Task.FromResult(new GatewaySendResult(EmdrStatus.Failed, null, EmdrGateways.As2NotAvailable, EmdrGateways.As2NotAvailable));
    public Task<IReadOnlyList<GatewayAck>> PollAsync(EmdrSubmission submission, DateTime nowUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GatewayAck>>([]);
}

[ScopedService]
public sealed class EmdrGateways
{
    public const string As2NotAvailable =
        "Direct AS2 sending isn't included in this release: it needs your organization's FDA ESG account, AS2 identifiers and exchanged certificates. Use Manual (ESG WebTrader) until it's set up.";

    public IReadOnlyList<IEmdrGateway> All { get; } = [new ManualEsgGateway(), new SimulatedEmdrGateway(), new As2EmdrGateway()];

    public IEmdrGateway? Get(string? key) => All.FirstOrDefault(g => g.Key == key);
}
