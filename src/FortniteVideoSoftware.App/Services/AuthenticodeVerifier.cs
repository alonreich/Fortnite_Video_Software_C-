// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FortniteVideoSoftware.App.Services;

/// <summary>
/// UPDATETRUST_01 — Authenticode verification for a file this process is about to EXECUTE.
///
/// <para>
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// WHY THIS EXISTS. <c>UpdateService</c> downloaded an .exe, checked its SHA-256 against a digest
/// read out of the SAME GitHub JSON document that supplied the download URL, and then launched it
/// with <c>--install --auto-update</c> and <c>UseShellExecute = true</c> — i.e. elevated, with the
/// "preserve your settings?" question force-answered YES.
///
/// That hash proves TRANSPORT INTEGRITY and nothing else: it proves the bytes match what that JSON
/// claimed, not that we published them. Anyone able to produce that response body — a compromised
/// repo or CI token, a TLS-terminating corporate proxy, a mis-issued certificate — controls the
/// payload AND the fingerprint that validates it, in one move. The end state is attacker-supplied
/// code running as Administrator behind a single UAC click.
///
/// The verification primitive already existed on the OTHER side of the pipe: <c>build/FvsBuild/
/// CodeSigning.cs</c> Authenticode-signs the shipped binary and then runs <c>signtool verify /pa</c>
/// over it. Nothing on the RECEIVING side ever looked. This class is that missing half.
/// ══════════════════════════════════════════════════════════════════════════════════════════════
/// </para>
///
/// <para>
/// <b>THE TRUST ANCHOR IS THE RUNNING PROCESS, NOT A HARDCODED THUMBPRINT.</b> A constant baked
/// into the source goes stale the day the signing certificate is renewed, and a stale pin fails
/// CLOSED — it would silently kill auto-update for every existing install until they manually
/// reinstalled. Instead the currently running executable is the anchor: it is already on the user's
/// disk, they already chose to run it, and it carries the publisher identity we want to stay with.
/// An update is accepted only when it is Authenticode-valid AND signed by the same publisher
/// subject as the binary asking for it.
/// </para>
///
/// <para>
/// <b>⚠️ UNSIGNED BUILDS — UPDATETRUST_02.</b> When the running process is itself unsigned there
/// is no anchor to compare against, so <see cref="EvaluateUpdateCandidate"/> returns
/// <see cref="TrustVerdict.NoAnchor"/>.
///
/// <para>
/// <b>That verdict is now a REFUSAL at the call site, not a fall-through.</b> It previously
/// degraded to hash-only "so nothing regresses for an unsigned distribution" — but the unsigned
/// distribution WAS production (see SIGNMANDATE_01 in <c>build/FvsBuild/CodeSigning.cs</c>), so the
/// degraded path was the only path that ever ran and the attack described at the top of this file
/// was never actually closed. <c>UpdateService</c> now deletes the download and tells the user to
/// install the signed build once by hand. <c>FVS_ALLOW_UNSIGNED_UPDATE=1</c> restores the old
/// behaviour for developers only.
/// </para>
///
/// <para>
/// This class's verdict is unchanged and deliberately stays a three-way classification rather than
/// a bool: WHAT to do about a missing anchor is policy, and policy belongs with the caller that
/// owns the consequence, not with the primitive that reads the signature.
/// </para>
/// </para>
///
/// <para>
/// <b>Revocation is deliberately NOT checked</b> (<c>WTD_REVOKE_NONE</c>). A revocation lookup
/// needs network at exactly the moment the machine may have none left, and an offline CRL/OCSP
/// fetch fails in a way indistinguishable from a real revocation — which would reject legitimate
/// updates for offline users. Publisher pinning against the running binary is the stronger control
/// here: defeating it requires our actual signing key, which revocation would not have helped with
/// in the window before it was noticed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class AuthenticodeVerifier
{
    /// <summary>The outcome of evaluating a downloaded file against the running process.</summary>
    internal enum TrustVerdict
    {
        /// <summary>Candidate is Authenticode-valid and signed by the anchor's publisher. Safe to run.</summary>
        Trusted,

        /// <summary>The ANCHOR is unsigned, so no publisher comparison is possible. Caller decides.</summary>
        NoAnchor,

        /// <summary>Candidate is unsigned, tampered, chain-invalid, or signed by a different publisher. REFUSE.</summary>
        Rejected
    }

    // ── WinVerifyTrust constants (wintrust.h) ────────────────────────────────────────────────
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 — {00AAC56B-CD44-11d0-8CC2-00C04FC295EE}.</summary>
    private static readonly Guid GenericVerifyV2 =
        new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pUnion;            // pFile, for dwUnionChoice == WTD_CHOICE_FILE
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>
    /// UPDATETRUST_01 — what a single file's signature actually is. Every field is safe to log.
    /// </summary>
    internal readonly record struct SignatureInfo(
        bool ChainValid,
        bool HasSignature,
        string Subject,
        string Thumbprint,
        string Detail);

    /// <summary>
    /// Runs the OS trust provider over <paramref name="filePath"/> and, when it carries a
    /// signature at all, reads the signer's subject and SHA-256 thumbprint.
    /// Never throws — an unreadable or unverifiable file comes back as "not valid, here is why".
    /// </summary>
    internal static SignatureInfo Inspect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new SignatureInfo(false, false, string.Empty, string.Empty, "File does not exist.");
        }

        int trustResult;
        try
        {
            trustResult = VerifyTrust(filePath);
        }
        catch (Exception ex)
        {
            // A missing wintrust.dll or a blocked entry point is NOT a pass.
            return new SignatureInfo(false, false, string.Empty, string.Empty,
                $"Trust provider unavailable ({ex.GetType().Name}: {ex.Message}).");
        }

        bool hasSignature = trustResult != TRUST_E_NOSIGNATURE;
        bool chainValid = trustResult == 0;

        string subject = string.Empty;
        string thumbprint = string.Empty;
        if (hasSignature)
        {
            try
            {
                // AOTSAFETY_05 / SYSLIB0057: the obsoletion directs callers to
                // X509CertificateLoader, which loads certificate FILES. It has no equivalent for
                // extracting an embedded signer certificate from a signed PE, which is what this
                // call does and what SYS-SIGNING needs. Suppressed for this one statement, with
                // the reason recorded, rather than project-wide — revisit if .NET ships a
                // replacement for reading Authenticode signers.
#pragma warning disable SYSLIB0057
                using X509Certificate signer = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                subject = signer.Subject ?? string.Empty;
                thumbprint = signer.GetCertHashString(HashAlgorithmName.SHA256) ?? string.Empty;
            }
            catch (Exception ex)
            {
                // Signed per the trust provider, but the certificate could not be read back. Treat
                // as unusable rather than as a pass — a publisher we cannot name is not a publisher
                // we can compare.
                chainValid = false;
                return new SignatureInfo(false, true, string.Empty, string.Empty,
                    $"Signature present but the signer certificate could not be read ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        return new SignatureInfo(chainValid, hasSignature, subject, thumbprint, DescribeTrustResult(trustResult));
    }

    /// <summary>
    /// UPDATETRUST_01 — the decision <c>UpdateService</c> asks for immediately before
    /// <c>Process.Start</c>. <paramref name="anchorPath"/> is the currently running executable.
    /// </summary>
    /// <param name="detail">A one-line, user-safe explanation of the verdict, always populated.</param>
    internal static TrustVerdict EvaluateUpdateCandidate(string candidatePath, string? anchorPath, out string detail)
    {
        SignatureInfo anchor = string.IsNullOrWhiteSpace(anchorPath)
            ? new SignatureInfo(false, false, string.Empty, string.Empty, "The running executable's path is unknown.")
            : Inspect(anchorPath!);

        if (!anchor.ChainValid || string.IsNullOrWhiteSpace(anchor.Subject))
        {
            // Unsigned (or unverifiable) distribution. There is nothing to pin to.
            detail = $"The running build is not signed ({anchor.Detail}), so the update cannot be checked against a publisher.";
            return TrustVerdict.NoAnchor;
        }

        SignatureInfo candidate = Inspect(candidatePath);

        if (!candidate.HasSignature)
        {
            detail = "The downloaded file carries no digital signature, but this build is signed. Refusing to run it.";
            return TrustVerdict.Rejected;
        }

        if (!candidate.ChainValid)
        {
            detail = $"The downloaded file's signature did not validate: {candidate.Detail}";
            return TrustVerdict.Rejected;
        }

        if (!string.Equals(candidate.Subject, anchor.Subject, StringComparison.OrdinalIgnoreCase))
        {
            detail = "The downloaded file is signed by a different publisher than this build. Refusing to run it.";
            return TrustVerdict.Rejected;
        }

        detail = $"Signature valid and publisher matches this build (sha256 {Shorten(candidate.Thumbprint)}).";
        return TrustVerdict.Trusted;
    }

    private static string Shorten(string thumbprint)
        => string.IsNullOrEmpty(thumbprint) ? "unknown" :
           thumbprint.Length <= 12 ? thumbprint : thumbprint[..12] + "…";

    private static string DescribeTrustResult(int hr) => hr switch
    {
        0 => "Signature is valid and the certificate chains to a trusted root.",
        TRUST_E_NOSIGNATURE => "The file is not signed.",
        TRUST_E_BAD_DIGEST => "The file has been modified since it was signed.",
        TRUST_E_EXPLICIT_DISTRUST => "The signature is explicitly distrusted on this machine.",
        TRUST_E_SUBJECT_NOT_TRUSTED => "The signer is not trusted on this machine.",
        CERT_E_UNTRUSTEDROOT => "The certificate does not chain to a trusted root.",
        CERT_E_CHAINING => "The certificate chain could not be built.",
        CERT_E_EXPIRED => "The signing certificate has expired.",
        _ => $"The trust provider returned 0x{hr:X8}."
    };

    /// <summary>
    /// The raw WinVerifyTrust round trip. Uses stack-allocated blittable structs and a pinned
    /// string so there is no marshalling helper to trim away under NativeAOT.
    /// The VERIFY call must always be paired with a CLOSE call or the provider leaks its state.
    /// </summary>
    private static int VerifyTrust(string filePath)
    {
        fixed (char* pPath = filePath)
        {
            WINTRUST_FILE_INFO fileInfo = default;
            fileInfo.cbStruct = (uint)sizeof(WINTRUST_FILE_INFO);
            fileInfo.pcwszFilePath = (nint)pPath;
            fileInfo.hFile = nint.Zero;
            fileInfo.pgKnownSubject = nint.Zero;

            WINTRUST_DATA data = default;
            data.cbStruct = (uint)sizeof(WINTRUST_DATA);
            data.dwUIChoice = WTD_UI_NONE;
            data.fdwRevocationChecks = WTD_REVOKE_NONE;
            data.dwUnionChoice = WTD_CHOICE_FILE;
            data.pUnion = (nint)(&fileInfo);
            data.dwStateAction = WTD_STATEACTION_VERIFY;
            data.dwProvFlags = WTD_SAFER_FLAG;

            Guid action = GenericVerifyV2;
            int result;
            try
            {
                result = WinVerifyTrust(nint.Zero, ref action, ref data);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                Guid closeAction = GenericVerifyV2;
                try { WinVerifyTrust(nint.Zero, ref closeAction, ref data); } catch { /* nothing left to do */ }
            }

            return result;
        }
    }
}
