using Servicedesk.Infrastructure.Mail.Graph;

namespace Servicedesk.Infrastructure.Mail.Ingest;

/// Classifies an inbound Graph message as an auto-generated / auto-reply mail
/// (out-of-office, bounce, autoresponder). Since v0.0.92 detection no longer
/// causes an outright skip: a detected mail that threads onto an existing
/// ticket is ingested normally but flagged, and the flag hard-suppresses any
/// trigger-sent mail back to the customer (loop prevention). Only a detected
/// mail with NO thread match is still skipped — an unsolicited auto-mail must
/// not open a ticket.
///
/// Signals, in evaluation order:
///  * Auto-Submitted / X-Auto-Submitted ≠ "no" (RFC 3834 — the canonical marker)
///  * X-Auto-Response-Suppress ≠ "None" (Microsoft/Exchange — sender asks us
///    not to auto-respond; honouring it doubles as loop prevention)
///  * Precedence: auto_reply / bulk / junk (pre-RFC-3834 convention)
///  * X-Autoreply / X-Autorespond present (non-standard autoresponders)
public static class AutoReplyDetector
{
    private static readonly string[] AutoPrecedence = { "auto_reply", "bulk", "junk" };

    /// Returns true when the message carries any auto-reply signal;
    /// <paramref name="signal"/> names the matched header + value so skip
    /// reasons and event metadata stay diagnosable.
    public static bool TryDetect(GraphFullMessage msg, out string signal)
    {
        if (!string.IsNullOrWhiteSpace(msg.AutoSubmitted)
            && !string.Equals(msg.AutoSubmitted.Trim(), "no", StringComparison.OrdinalIgnoreCase))
        {
            signal = $"Auto-Submitted: {msg.AutoSubmitted.Trim()}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(msg.AutoResponseSuppress)
            && !string.Equals(msg.AutoResponseSuppress.Trim(), "None", StringComparison.OrdinalIgnoreCase))
        {
            signal = $"X-Auto-Response-Suppress: {msg.AutoResponseSuppress.Trim()}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(msg.Precedence)
            && AutoPrecedence.Contains(msg.Precedence.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            signal = $"Precedence: {msg.Precedence.Trim()}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(msg.XAutoReply))
        {
            signal = $"X-Autoreply: {msg.XAutoReply.Trim()}";
            return true;
        }

        signal = string.Empty;
        return false;
    }
}
