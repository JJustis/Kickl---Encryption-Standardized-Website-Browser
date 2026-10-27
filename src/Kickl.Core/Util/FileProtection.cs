using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kickl.Core.Util;

/// <summary>
/// Locks a key file or key folder down to the account that owns it. On Windows that replaces the inherited
/// permissions with exactly three allowed principals: the current user, the local system account and administrators
/// (the same set Windows itself can always override). On Linux and macOS it is mode 0600 for files and 0700 for folders.
/// A key that other local users can read is not protected by the encryption around it.
/// </summary>
public static class FileProtection
{
    /// <summary>Restrict <paramref name="path"/> (a file or a folder). A path that does not exist is ignored.</summary>
    public static void Restrict(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            return;
        }

        try
        {
            File.SetUnixFileMode(path, Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // a filesystem without Unix modes (for example some removable media): nothing more can be done
        }
    }

    /// <summary>Restrict a folder and every file already in it.</summary>
    public static void RestrictTree(string directory)
    {
        if (!Directory.Exists(directory)) return;
        Restrict(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) Restrict(file);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var allowed = new[]
        {
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);   // drop Users, Authenticated Users, Everyone
            foreach (var sid in allowed)
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Apply(path, () => new DirectoryInfo(path).SetAccessControl(security));
        }
        else
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in allowed)
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            Apply(path, () => new FileInfo(path).SetAccessControl(security));
        }
    }

    /// <summary>
    /// Overwrite a file with random bytes, then delete it. Best effort: it stops a casual undelete, but a journaling
    /// or flash filesystem may still hold old blocks, which is why key files are also wrapped, not just deleted.
    /// </summary>
    public static void Shred(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var length = new FileInfo(path).Length;
            File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)Math.Min(length, int.MaxValue)));
        }
        catch (IOException) { }
        File.Delete(path);
    }

    [SupportedOSPlatform("windows")]
    private static void Apply(string path, Action set)
    {
        try
        {
            set();
        }
        catch (NotSupportedException)
        {
            // a filesystem with no permissions at all (FAT/exFAT removable media, where an offline root key often lives):
            // there is nothing to restrict, and refusing to save the key would be worse
        }
        catch (UnauthorizedAccessException ex)
        {
            // Usually a folder created with elevated rights, whose permissions this account may not change. Say so, instead of
            // leaving a key quietly readable by every local user.
            throw new UnauthorizedAccessException(
                $"Cannot restrict '{path}' to its owner: this account may not change its permissions. Keep keys in a folder this account owns.", ex);
        }
    }

    /// <summary>Who may open <paramref name="path"/>, for tests and health checks. Windows only; null elsewhere.</summary>
    public static IReadOnlyList<string>? AllowedPrincipals(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return AllowedWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> AllowedWindows(string path)
    {
        AuthorizationRuleCollection rules = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            : new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        return rules.Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value)
            .Distinct()
            .ToList();
    }
}
