using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GueibsOptimizer.Core;

/// <summary>Verificação de assinatura digital (WinVerifyTrust) antes de executar um binário baixado.</summary>
public static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>True se a assinatura é válida e confiável para o Windows.</summary>
    public static bool IsTrusted(string path) => Verify(path) == 0;

    /// <summary>Código bruto do WinVerifyTrust (0 = confiável).</summary>
    internal static int Verify(string path)
    {
        var file = new WinTrustFileInfo(path);
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData(filePtr);
            int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    /// <summary>
    /// Subject completo do certificado do assinante (inclui CN e O=organização) ou null se não for assinado.
    /// Use a organização para checar a empresa: o CN pode ser só o nome do produto (ex.: ".NET").
    /// </summary>
    public static string? SignerName(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo(string path)
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public string FilePath = path;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData(IntPtr fileInfo)
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
        public IntPtr PolicyCallbackData = IntPtr.Zero;
        public IntPtr SipClientData = IntPtr.Zero;
        public uint UiChoice = 2;            // WTD_UI_NONE
        public uint RevocationChecks = 0;    // WTD_REVOKE_NONE (funciona offline)
        public uint UnionChoice = 1;         // WTD_CHOICE_FILE
        public IntPtr FileInfoPtr = fileInfo;
        public uint StateAction = 1;         // WTD_STATEACTION_VERIFY
        public IntPtr StateData = IntPtr.Zero;
        public string? UrlReference;
        public uint ProvFlags = 0x00000080;  // WTD_CACHE_ONLY_URL_RETRIEVAL
        public uint UiContext;
        public IntPtr SignatureSettings = IntPtr.Zero;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, [In, Out] WinTrustData data);
}
