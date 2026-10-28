using System.Text.Json.Serialization;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>
/// The paired TV in config.json (<c>tv</c>). <see cref="ClientKey"/> is stored encrypted for the Windows user (DPAPI, "dpapi:…" like the
/// Home Assistant token); it never goes to the UI, the log or the phone remote. The certificate pin and the MAC are no secrets.
/// </summary>
public sealed class TvConfig
{
    /// <summary>"lg-webos", or empty when no TV is paired.</summary>
    public string Provider { get; set; } = "";

    /// <summary>The TV's IP address in the local network (an IP literal, never a host name).</summary>
    public string Address { get; set; } = "";

    /// <summary>Paired over TLS (port 3001). A TLS pairing is never used without TLS.</summary>
    public bool Secure { get; set; } = true;

    /// <summary>The pairing key, DPAPI-protected.</summary>
    public string ClientKey { get; set; } = "";

    /// <summary>SHA-256 of the TV certificate's public key (base64), pinned at pairing.</summary>
    public string CertPin { get; set; } = "";

    /// <summary>The TV's MAC for Wake-on-LAN ("AA:BB:CC:DD:EE:FF"), learned at pairing or entered by hand.</summary>
    public string Mac { get; set; } = "";

    public string Name { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>The app in front on the TV while pairing (the PC's input), for "TV off with the PC".</summary>
    public string PcApp { get; set; } = "";

    /// <summary>Show TV rows in the Guide menu (when a TV is paired).</summary>
    public bool InGuide { get; set; } = true;

    /// <summary>Switch the TV on when CouchHUD starts or the PC wakes up (Wake-on-LAN). Expert, off by default.</summary>
    public bool OnWithCouchHud { get; set; }

    /// <summary>Switch the TV off when the PC goes to sleep or shuts down from CouchHUD (only while the PC's input is in front). Expert, off by default.</summary>
    public bool OffWithPc { get; set; }

    /// <summary>
    /// Addresses and MACs that were ever paired over TLS (at most <see cref="MaxSecureSeen"/>, newest last). Not cleared by "Remove TV":
    /// an unencrypted pairing with one of them is refused (a downgrade by someone in the network who blocks the TLS port).
    /// </summary>
    public List<string> SecureSeen { get; set; } = [];

    public const int MaxSecureSeen = 32;

    /// <summary>Adds to <see cref="SecureSeen"/> (no duplicates, oldest dropped beyond the limit).</summary>
    public void RememberSecure(IEnumerable<string> ids)
    {
        SecureSeen ??= [];
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) continue;
            SecureSeen.Remove(id);
            SecureSeen.Add(id);
        }
        if (SecureSeen.Count > MaxSecureSeen) SecureSeen.RemoveRange(0, SecureSeen.Count - MaxSecureSeen);
    }

    [JsonIgnore]
    public TvFollow Follow => new(OnWithCouchHud, OffWithPc);

    /// <summary>The stored pairing with the key decrypted by <paramref name="unprotect"/>; null when nothing (usable) is stored.</summary>
    public TvPairing? ToPairing(Func<string, string> unprotect)
    {
        if (Provider.Length == 0 || Address.Length == 0 || ClientKey.Length == 0) return null;
        var key = unprotect(ClientKey);
        if (key.Length == 0) return null;
        static string? Opt(string s) => s.Length == 0 ? null : s;
        return new TvPairing(Provider, Address, Secure, key, Opt(CertPin), Opt(Mac), Opt(Name), Opt(Model), Opt(PcApp));
    }

    /// <summary>Stores a pairing (key through <paramref name="protect"/>) or clears it; the follow and Guide options stay.</summary>
    public void SetPairing(TvPairing? p, Func<string, string> protect)
    {
        Provider = p?.Provider ?? "";
        Address = p?.Address ?? "";
        Secure = p?.Secure ?? true;
        ClientKey = p is null ? "" : protect(p.ClientKey);
        CertPin = p?.CertPin ?? "";
        Mac = p?.Mac ?? "";
        Name = p?.Name ?? "";
        Model = p?.Model ?? "";
        PcApp = p?.PcApp ?? "";
    }
}
