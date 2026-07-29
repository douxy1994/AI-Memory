using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Security.Credentials;

namespace AIMemory.Windows.Services;

public sealed class CredentialService
{
    private const string Resource = "com.aimemory.windows.webdav";
    private const string ChatMemResource = "com.chatmem.app.webdav";
    private const uint GenericCredentialType = 1;
    private readonly PasswordVault _vault = new();

    public void Save(string username, string password)
    {
        RemoveAll();
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
        {
            _vault.Add(new PasswordCredential(Resource, username, password));
        }
    }

    public (string Username, string Password)? Load(string? username = null)
    {
        var credential = _vault.RetrieveAll()
            .FirstOrDefault(value =>
                value.Resource == Resource
                && (string.IsNullOrWhiteSpace(username)
                    || string.Equals(
                        value.UserName,
                        username,
                        StringComparison.Ordinal)));
        if (credential is null)
        {
            return null;
        }
        credential.RetrievePassword();
        return (credential.UserName, credential.Password);
    }

    /// <summary>
    /// Reads ChatMem's Rust keyring entry without modifying it. keyring-rs
    /// maps Entry::new(service, user) to the Generic Credential target
    /// "user.service" on Windows and stores password text as UTF-16LE.
    /// </summary>
    public string? LoadLegacyChatMemPassword(string username)
    {
        username = username.Trim();
        if (username.Length == 0) return null;

        var target = $"{username}.{ChatMemResource}";
        if (!CredRead(
                target,
                GenericCredentialType,
                0,
                out var credentialPointer))
        {
            return null;
        }

        try
        {
            var credential =
                Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize == 0
                || credential.CredentialBlobSize % 2 != 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(
                    credential.CredentialBlob,
                    bytes,
                    0,
                    bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private void RemoveAll()
    {
        foreach (var credential in _vault.RetrieveAll()
                     .Where(value => value.Resource == Resource))
        {
            _vault.Remove(credential);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
