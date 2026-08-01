#if NET // LibraryImport (source-gen, .NET 7+); the netfx build has no SChannel filter yet.
using System.Runtime.InteropServices;

namespace SocketSets.Tls.SChannel;

/// <summary>
/// Minimal P/Invoke surface for SSPI/SChannel — Windows' in-box TLS engine, reached through the same
/// Security Support Provider Interface that Kerberos/NTLM use. Only the functions the push filter needs.
///
/// The important property, and the reason this exists at all: SSPI's SChannel provider is a pure
/// buffer-in/buffer-out state machine, exactly like an OpenSSL memory-BIO pair. Nothing here touches a
/// socket — you hand <see cref="InitializeSecurityContext"/> / <see cref="DecryptMessage"/> the bytes you
/// received and it hands back the bytes to send. That is what lets Windows TLS sit in a push loop without
/// the pull-shaped inversion an <c>SslStream</c> would force (see <see cref="SChannelTlsFilter"/>).
///
/// Everything is declared blittable (raw pointers, no string marshalling) so the generated stubs are
/// direct calls — <see cref="EncryptMessage"/> and <see cref="DecryptMessage"/> run once per TLS record,
/// which is per-write and per-read on a hot connection.
/// </summary>
internal static unsafe partial class NativeSspi
{
    // sspicli.dll is where the SSPI entry points actually live on modern Windows; secur32.dll is a
    // forwarder kept for compatibility. Bind the real one, as the framework itself does.
    private const string Sspi = "sspicli.dll";
    private const string Crypt32 = "crypt32.dll";

    /// <summary>UNISP_NAME_W: the "Microsoft Unified Security Protocol Provider" package — SChannel,
    /// negotiating whichever of SSL/TLS the credential and OS policy allow.</summary>
    internal const string UnispName = "Microsoft Unified Security Protocol Provider";

    // ===================== SECURITY_STATUS =====================
    // Success/informational codes are 0x0009xxxx; failures are 0x8009xxxx. Note the deliberate trap
    // pairing of SEC_I_RENEGOTIATE (0x00090321) and SEC_E_BUFFER_TOO_SMALL (0x80090321): same low bits,
    // opposite severity, so always compare the whole value.
    internal const int SEC_E_OK = 0x00000000;
    internal const int SEC_I_CONTINUE_NEEDED = 0x00090312;
    internal const int SEC_I_COMPLETE_NEEDED = 0x00090313;
    internal const int SEC_I_COMPLETE_AND_CONTINUE = 0x00090314;
    internal const int SEC_I_CONTEXT_EXPIRED = 0x00090317; // peer sent close_notify
    internal const int SEC_I_INCOMPLETE_CREDENTIALS = 0x00090320;
    internal const int SEC_I_RENEGOTIATE = 0x00090321;
    internal const int SEC_E_INCOMPLETE_MESSAGE = unchecked((int)0x80090318);
    internal const int SEC_E_BUFFER_TOO_SMALL = unchecked((int)0x80090321);
    internal const int SEC_E_CONTEXT_EXPIRED = unchecked((int)0x80090317);
    internal const int SEC_E_MESSAGE_ALTERED = unchecked((int)0x8009030F);
    internal const int SEC_E_UNTRUSTED_ROOT = unchecked((int)0x80090325);
    internal const int SEC_E_ILLEGAL_MESSAGE = unchecked((int)0x80090326);
    internal const int SEC_E_CERT_UNKNOWN = unchecked((int)0x80090327);
    internal const int SEC_E_CERT_EXPIRED = unchecked((int)0x80090328);
    internal const int SEC_E_ALGORITHM_MISMATCH = unchecked((int)0x80090331);
    internal const int SEC_E_UNKNOWN_CREDENTIALS = unchecked((int)0x8009030D);
    internal const int SEC_E_NO_CREDENTIALS = unchecked((int)0x8009030E);
    internal const int SEC_E_DECRYPT_FAILURE = unchecked((int)0x80090330);
    internal const int SEC_E_WRONG_PRINCIPAL = unchecked((int)0x80090322);

    // ===================== SecBuffer types =====================
    internal const int SECBUFFER_VERSION = 0;
    internal const int SECBUFFER_EMPTY = 0;
    internal const int SECBUFFER_DATA = 1;
    internal const int SECBUFFER_TOKEN = 2;
    internal const int SECBUFFER_MISSING = 4;         // cbBuffer = bytes still needed (a hint)
    internal const int SECBUFFER_EXTRA = 5;           // trailing bytes SChannel did not consume
    internal const int SECBUFFER_STREAM_TRAILER = 6;
    internal const int SECBUFFER_STREAM_HEADER = 7;
    internal const int SECBUFFER_ALERT = 17;
    internal const int SECBUFFER_APPLICATION_PROTOCOLS = 18; // ALPN (see the TODO in SChannelTlsFilter)

    // ===================== credential use =====================
    internal const int SECPKG_CRED_INBOUND = 1;  // server
    internal const int SECPKG_CRED_OUTBOUND = 2; // client

    // ===================== ISC_REQ_* (client) =====================
    internal const int ISC_REQ_REPLAY_DETECT = 0x00000004;
    internal const int ISC_REQ_SEQUENCE_DETECT = 0x00000008;
    internal const int ISC_REQ_CONFIDENTIALITY = 0x00000010;
    internal const int ISC_REQ_USE_SUPPLIED_CREDS = 0x00000080;
    internal const int ISC_REQ_ALLOCATE_MEMORY = 0x00000100;
    internal const int ISC_REQ_EXTENDED_ERROR = 0x00004000;
    internal const int ISC_REQ_STREAM = 0x00008000;
    internal const int ISC_REQ_MANUAL_CRED_VALIDATION = 0x00080000;

    // ===================== ASC_REQ_* (server) — NOT the same bits as ISC_REQ_* =====================
    internal const int ASC_REQ_REPLAY_DETECT = 0x00000004;
    internal const int ASC_REQ_SEQUENCE_DETECT = 0x00000008;
    internal const int ASC_REQ_CONFIDENTIALITY = 0x00000010;
    internal const int ASC_REQ_ALLOCATE_MEMORY = 0x00000100;
    internal const int ASC_REQ_EXTENDED_ERROR = 0x00008000;
    internal const int ASC_REQ_STREAM = 0x00010000;

    // ===================== SCH_CREDENTIALS =====================
    internal const int SCH_CREDENTIALS_VERSION = 5;
    internal const int SCH_CRED_FORMAT_CERT_CONTEXT = 0;
    internal const int SCH_CRED_MANUAL_CRED_VALIDATION = 0x00000008;
    internal const int SCH_CRED_NO_DEFAULT_CREDS = 0x00000010;
    internal const int SCH_CRED_AUTO_CRED_VALIDATION = 0x00000020;
    internal const int SCH_USE_STRONG_CRYPTO = 0x00400000;

    // SP_PROT_* protocol bits. TLS_PARAMETERS.grbitDisabledProtocols is an INVERTED mask — a set bit
    // DISABLES that protocol — which is the classic way to accidentally turn SSL3 back on. Both the
    // client and server bit of each version are listed; setting the side that doesn't apply is harmless.
    internal const int SP_PROT_SSL2 = 0x00000004 | 0x00000008;
    internal const int SP_PROT_SSL3 = 0x00000010 | 0x00000020;
    internal const int SP_PROT_TLS1_0 = 0x00000040 | 0x00000080;
    internal const int SP_PROT_TLS1_1 = 0x00000100 | 0x00000200;
    internal const int SP_PROT_TLS1_2 = 0x00000400 | 0x00000800;
    internal const int SP_PROT_TLS1_3 = 0x00001000 | 0x00002000;

    /// <summary>Disable everything below TLS 1.2, leaving 1.2/1.3 to negotiate under OS policy. TLS 1.3
    /// itself needs Windows 11 / Server 2022 (client and server); older builds simply settle on 1.2.</summary>
    internal const int SP_PROT_DISABLE_BELOW_TLS1_2 = SP_PROT_SSL2 | SP_PROT_SSL3 | SP_PROT_TLS1_0 | SP_PROT_TLS1_1;

    /// <summary>Disable everything below TLS 1.3, i.e. TLS 1.3 only. The OpenSSL provider's default floor,
    /// mirrored here — see <see cref="SocketSets.Tls.TlsProtocol"/> for why 1.3 is the default rather than
    /// 1.2. NOTE the platform floor this implies: SChannel only speaks TLS 1.3 on Windows 11 / Server 2022
    /// and later, so on an older build this disables every protocol the OS has and the handshake fails
    /// outright rather than quietly settling on 1.2. That is the intended shape (a silent downgrade is the
    /// failure mode a floor exists to prevent), but it is why the failure is a LOUD one.</summary>
    internal const int SP_PROT_DISABLE_BELOW_TLS1_3 = SP_PROT_DISABLE_BELOW_TLS1_2 | SP_PROT_TLS1_2;

    // ===================== ALPN =====================
    // SEC_APPLICATION_PROTOCOL_NEGOTIATION_EXT
    internal const int SecApplicationProtocolNegotiationExt_None = 0;
    internal const int SecApplicationProtocolNegotiationExt_NPN = 1;
    internal const int SecApplicationProtocolNegotiationExt_ALPN = 2;

    // SEC_APPLICATION_PROTOCOL_NEGOTIATION_STATUS
    internal const int SecApplicationProtocolNegotiationStatus_None = 0;
    internal const int SecApplicationProtocolNegotiationStatus_Success = 1;
    internal const int SecApplicationProtocolNegotiationStatus_SelectedClientOnly = 2;

    /// <summary>Bytes of fixed header in front of the protocol list in a SECBUFFER_APPLICATION_PROTOCOLS
    /// buffer: SEC_APPLICATION_PROTOCOLS.ProtocolListsSize (4) + ProtoNegoExt (4) + ProtocolListSize (2).
    /// The list that follows is the ordinary RFC 7301 encoding — see <see cref="AlpnProtocolList"/>.</summary>
    internal const int AlpnHeaderSize = 10;

    // ===================== QueryContextAttributes ids =====================
    internal const int SECPKG_ATTR_STREAM_SIZES = 4;
    internal const int SECPKG_ATTR_APPLICATION_PROTOCOL = 35;
    internal const int SECPKG_ATTR_REMOTE_CERT_CONTEXT = 0x53;
    internal const int SECPKG_ATTR_CONNECTION_INFO = 0x5A;

    /// <summary>ApplyControlToken command: start an orderly TLS shutdown (queues the close_notify that the
    /// next InitializeSecurityContext/AcceptSecurityContext call actually emits).</summary>
    internal const int SCHANNEL_SHUTDOWN = 1;

    // ===================== calls =====================

    [LibraryImport(Sspi, EntryPoint = "AcquireCredentialsHandleW")]
    internal static partial int AcquireCredentialsHandle(
        char* pszPrincipal, char* pszPackage, int fCredentialUse,
        void* pvLogonId, void* pAuthData, void* pGetKeyFn, void* pvGetKeyArgument,
        SecHandle* phCredential, long* ptsExpiry);

    [LibraryImport(Sspi, EntryPoint = "FreeCredentialsHandle")]
    internal static partial int FreeCredentialsHandle(SecHandle* phCredential);

    /// <summary>Client half of the handshake. <paramref name="pszTargetName"/> is both the SNI name sent to
    /// the server and the name SChannel would validate against (we validate ourselves instead — see
    /// <see cref="SCH_CRED_MANUAL_CRED_VALIDATION"/>).</summary>
    [LibraryImport(Sspi, EntryPoint = "InitializeSecurityContextW")]
    internal static partial int InitializeSecurityContext(
        SecHandle* phCredential, SecHandle* phContext, char* pszTargetName,
        int fContextReq, int Reserved1, int TargetDataRep,
        SecBufferDesc* pInput, int Reserved2,
        SecHandle* phNewContext, SecBufferDesc* pOutput,
        int* pfContextAttr, long* ptsExpiry);

    /// <summary>Server half of the handshake. Same buffer protocol as
    /// <see cref="InitializeSecurityContext"/>, minus the target name.</summary>
    [LibraryImport(Sspi, EntryPoint = "AcceptSecurityContext")]
    internal static partial int AcceptSecurityContext(
        SecHandle* phCredential, SecHandle* phContext, SecBufferDesc* pInput,
        int fContextReq, int TargetDataRep,
        SecHandle* phNewContext, SecBufferDesc* pOutput,
        int* pfContextAttr, long* ptsTimeStamp);

    /// <summary>Encrypt one record IN PLACE across a 4-buffer descriptor (header / data / trailer / empty)
    /// that all points into one contiguous span — the zero-copy shape the outbound path relies on.</summary>
    [LibraryImport(Sspi, EntryPoint = "EncryptMessage")]
    internal static partial int EncryptMessage(SecHandle* phContext, int fQOP, SecBufferDesc* pMessage, int MessageSeqNo);

    /// <summary>Decrypt IN PLACE: pass one SECBUFFER_DATA over the raw ciphertext; on return the descriptor
    /// is rewritten to point at the plaintext (a sub-range of that same buffer) plus any SECBUFFER_EXTRA.</summary>
    [LibraryImport(Sspi, EntryPoint = "DecryptMessage")]
    internal static partial int DecryptMessage(SecHandle* phContext, SecBufferDesc* pMessage, int MessageSeqNo, int* pfQOP);

    [LibraryImport(Sspi, EntryPoint = "QueryContextAttributesW")]
    internal static partial int QueryContextAttributes(SecHandle* phContext, int ulAttribute, void* pBuffer);

    [LibraryImport(Sspi, EntryPoint = "ApplyControlToken")]
    internal static partial int ApplyControlToken(SecHandle* phContext, SecBufferDesc* pInput);

    [LibraryImport(Sspi, EntryPoint = "FreeContextBuffer")]
    internal static partial int FreeContextBuffer(void* pvContextBuffer);

    [LibraryImport(Sspi, EntryPoint = "DeleteSecurityContext")]
    internal static partial int DeleteSecurityContext(SecHandle* phContext);

    [LibraryImport(Crypt32, EntryPoint = "CertFreeCertificateContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CertFreeCertificateContext(nint pCertContext);

    /// <summary>Human-readable name for the SECURITY_STATUS values worth recognising on sight; anything
    /// else falls back to hex. Diagnostics only.</summary>
    internal static string Describe(int status) => status switch
    {
        SEC_E_OK => "SEC_E_OK",
        SEC_I_CONTINUE_NEEDED => "SEC_I_CONTINUE_NEEDED",
        SEC_I_CONTEXT_EXPIRED => "SEC_I_CONTEXT_EXPIRED",
        SEC_I_INCOMPLETE_CREDENTIALS => "SEC_I_INCOMPLETE_CREDENTIALS",
        SEC_I_RENEGOTIATE => "SEC_I_RENEGOTIATE",
        SEC_E_INCOMPLETE_MESSAGE => "SEC_E_INCOMPLETE_MESSAGE",
        SEC_E_BUFFER_TOO_SMALL => "SEC_E_BUFFER_TOO_SMALL",
        SEC_E_CONTEXT_EXPIRED => "SEC_E_CONTEXT_EXPIRED",
        SEC_E_MESSAGE_ALTERED => "SEC_E_MESSAGE_ALTERED",
        SEC_E_UNTRUSTED_ROOT => "SEC_E_UNTRUSTED_ROOT",
        SEC_E_ILLEGAL_MESSAGE => "SEC_E_ILLEGAL_MESSAGE",
        SEC_E_CERT_UNKNOWN => "SEC_E_CERT_UNKNOWN",
        SEC_E_CERT_EXPIRED => "SEC_E_CERT_EXPIRED",
        SEC_E_ALGORITHM_MISMATCH => "SEC_E_ALGORITHM_MISMATCH",
        SEC_E_UNKNOWN_CREDENTIALS => "SEC_E_UNKNOWN_CREDENTIALS",
        SEC_E_NO_CREDENTIALS => "SEC_E_NO_CREDENTIALS",
        SEC_E_DECRYPT_FAILURE => "SEC_E_DECRYPT_FAILURE",
        SEC_E_WRONG_PRINCIPAL => "SEC_E_WRONG_PRINCIPAL",
        _ => $"0x{status:X8}",
    };
}

/// <summary>CredHandle / CtxtHandle — an opaque two-word SSPI handle.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecHandle
{
    public nint dwLower;
    public nint dwUpper;
}

/// <summary>One buffer in an SSPI buffer descriptor: a length, a <c>SECBUFFER_*</c> type tag, and a
/// pointer. SSPI both reads these and REWRITES them (type and length) to report what it did.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SecBuffer
{
    public int cbBuffer;
    public int BufferType;
    public byte* pvBuffer;
}

/// <summary>A vector of <see cref="SecBuffer"/>s — SSPI's equivalent of a scatter/gather list.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SecBufferDesc
{
    public int ulVersion;
    public int cBuffers;
    public SecBuffer* pBuffers;
}

/// <summary>SecPkgContext_StreamSizes: the record-layer geometry, valid only once the handshake completes.
/// <c>cbHeader</c> + payload + <c>cbTrailer</c> is the on-the-wire size of one record, which is exactly the
/// span the outbound path reserves so SChannel can frame in place.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecPkgContextStreamSizes
{
    public int cbHeader;
    public int cbTrailer;
    public int cbMaximumMessage; // 16384 — the TLS plaintext record limit
    public int cBuffers;
    public int cbBlockSize;
}

/// <summary>SecPkgContext_ConnectionInfo — what was actually negotiated. Only <c>dwProtocol</c> is read
/// here (an SP_PROT_* bit, so the client and server bit of a version are distinct values — test with a
/// MASK, never equality), but the whole struct must be declared or SSPI writes past the buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecPkgContextConnectionInfo
{
    public int dwProtocol;
    public int aiCipher;
    public int dwCipherStrength;
    public int aiHash;
    public int dwHashStrength;
    public int aiExch;
    public int dwExchStrength;
}

/// <summary>SecPkgContext_ApplicationProtocol — the ALPN outcome, read back with
/// <see cref="NativeSspi.SECPKG_ATTR_APPLICATION_PROTOCOL"/> once the handshake completes. Only meaningful
/// when <c>ProtoNegoStatus</c> is
/// <see cref="NativeSspi.SecApplicationProtocolNegotiationStatus_Success"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SecPkgContextApplicationProtocol
{
    public int ProtoNegoStatus;
    public int ProtoNegoExt;
    public byte ProtocolIdSize;
    public fixed byte ProtocolId[AlpnProtocolList.MaxProtocolIdLength];
}

/// <summary>SCH_CREDENTIALS (version 5) — the modern SChannel credential struct. The older SCHANNEL_CRED
/// cannot negotiate TLS 1.3 at all, which is the only reason to care which one you use.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SchCredentials
{
    public int dwVersion;
    public int dwCredFormat;
    public int cCreds;
    public nint* paCred; // PCCERT_CONTEXT[]
    public nint hRootStore;
    public int cMappers;
    public nint aphMappers;
    public int dwSessionLifespan;
    public int dwFlags;
    public int cTlsParameters;
    public TlsParameters* pTlsParameters;
}

/// <summary>TLS_PARAMETERS — per-credential protocol/cipher restrictions. See
/// <see cref="NativeSspi.SP_PROT_DISABLE_BELOW_TLS1_2"/> for the inverted-mask warning.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TlsParameters
{
    public int cAlpnIds;
    public nint rgstrAlpnIds;
    public int grbitDisabledProtocols;
    public int cDisabledCrypto;
    public nint pDisabledCrypto;
    public int dwFlags;
}
#endif
