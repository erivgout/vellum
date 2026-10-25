using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vellum.Domain;

namespace Vellum.Data;

/// <summary>INV-055: the hash of an audit entry covers its content and the previous entry's hash.</summary>
public static class AuditHashing
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(AuditEntry e)
    {
        var sb = new StringBuilder();
        void Add(object? v) => sb.Append(v switch
        {
            null => "",
            DateTime d => d.Ticks.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        }).Append('\u001f');

        Add(e.PrevHash); Add(e.RecordId); Add(e.Seq); Add(e.At); Add(e.UserId); Add(e.UserName); Add(e.OnBehalfOfId);
        Add((int)e.ActorKind); Add(e.Source); Add(e.Action); Add(e.FieldKey); Add(e.OldValue); Add(e.NewValue);
        Add(e.Detail); Add(e.Reason); Add(e.PhaseName); Add(e.RecordVersion);
        return Sha256Hex(sb.ToString());
    }

    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>Verify one chain (entries in Seq order). Returns the first broken entry, or null when intact.</summary>
    public static AuditEntry? FirstBreak(IEnumerable<AuditEntry> ordered)
    {
        var prev = Genesis;
        long seq = 0;
        var lastAt = DateTime.MinValue;
        foreach (var e in ordered)
        {
            if (e.Seq != seq + 1 || e.PrevHash != prev || e.At < lastAt || Compute(e) != e.Hash) return e;
            prev = e.Hash;
            seq = e.Seq;
            lastAt = e.At;
        }
        return null;
    }
}
