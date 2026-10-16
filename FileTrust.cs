using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DSHGuard;

/// <summary>
/// 下载来的可执行文件在交给系统运行之前的完整性 / 来源校验（2026-10-16 审计 H1）。
/// <para>
/// 两件事分开：<b>摘要</b>证明"拿到的字节就是清单里说的那份"；<b>Authenticode 签名</b>证明
/// "这份字节出自签名者、且签完没被改过"。签名缺席不等于有问题（个人项目与很多 nightly
/// 安装包本来就不签），但<b>签名存在却校验不过</b>一定是被改过 ⇒ 一律拒绝执行。
/// </para>
/// </summary>
internal static class FileTrust
{
    internal enum SignatureState
    {
        /// <summary>签名存在且链校验通过。</summary>
        Valid,
        /// <summary>文件没有签名（不是错误，只是没有这层保证）。</summary>
        NotSigned,
        /// <summary>签名存在但校验失败（被篡改 / 证书吊销或不受信）——必须拒绝。</summary>
        Invalid,
        /// <summary>非 Windows 或调用失败，判不了（调用方按"无签名保证"处理，不当成通过）。</summary>
        Unknown,
    }

    /// <summary>SHA-256 小写十六进制；读不了返回空串。</summary>
    internal static string Sha256Hex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>SHA-512 的 Base64（electron-updater 清单用的就是这个格式）；读不了返回空串。</summary>
    internal static string Sha512Base64(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToBase64String(SHA512.HashData(fs));
        }
        catch { return ""; }
    }

    // ── WinVerifyTrust（wintrust.dll）──
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    private const uint TRUST_E_PROVIDER_UNKNOWN = 0x800B0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;           // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks;  // 0 = WTD_REVOKE_NONE（离线也能判；吊销由链策略兜）
        public uint dwUnionChoice;        // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;        // 1 = VERIFY, 2 = CLOSE
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    /// <summary>校验文件的 Authenticode 签名（绝不抛）。</summary>
    internal static SignatureState VerifySignature(string path)
    {
        if (!OperatingSystem.IsWindows()) return SignatureState.Unknown;
        IntPtr pPath = IntPtr.Zero, pFile = IntPtr.Zero, pData = IntPtr.Zero;
        try
        {
            pPath = Marshal.StringToCoTaskMemUni(path);
            var fi = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pPath,
            };
            pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fi, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = pFile,
                dwStateAction = 1,
            };
            pData = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, false);

            uint hr = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

            // 释放验证状态
            data = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
            data.dwStateAction = 2;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, pData);

            if (hr == 0) return SignatureState.Valid;
            if (hr == TRUST_E_NOSIGNATURE || hr == TRUST_E_SUBJECT_FORM_UNKNOWN || hr == TRUST_E_PROVIDER_UNKNOWN)
                return SignatureState.NotSigned;
            return SignatureState.Invalid;
        }
        catch { return SignatureState.Unknown; }
        finally
        {
            if (pData != IntPtr.Zero) Marshal.FreeCoTaskMem(pData);
            if (pFile != IntPtr.Zero) Marshal.FreeCoTaskMem(pFile);
            if (pPath != IntPtr.Zero) Marshal.FreeCoTaskMem(pPath);
        }
    }

    /// <summary>界面 / 日志用的中文说明。</summary>
    internal static string Describe(SignatureState s) => s switch
    {
        SignatureState.Valid => "数字签名有效",
        SignatureState.NotSigned => "无数字签名",
        SignatureState.Invalid => "数字签名校验失败",
        _ => "数字签名未能判定",
    };
}
