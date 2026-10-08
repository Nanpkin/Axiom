using System.Security.AccessControl;
using System.Security.Principal;
using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal static class RuleDraftDataPath
{
    internal static string Prepare(string sid)
    {
        Directory.CreateDirectory(AxiomUserData.Root);
        if ((File.GetAttributes(AxiomUserData.Root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("User data directory cannot be a reparse point.");
        if ((File.GetAttributes(AxiomUserData.Root) & FileAttributes.Encrypted) != 0)
            File.Decrypt(AxiomUserData.Root);
        var root = Path.Combine(AxiomUserData.Root, "preview-rule-drafts");
        Directory.CreateDirectory(root);
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Rule database directory cannot be a reparse point.");
        if ((directory.Attributes & FileAttributes.Encrypted) != 0) File.Decrypt(root);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        // Some managed/restricted desktops deny rewriting an ACL even when the
        // current user already owns the directory. Existing data remains usable;
        // do not make ordinary startup depend on this optional hardening step.
        try { directory.SetAccessControl(security); }
        catch (UnauthorizedAccessException) when (WindowsIdentity.GetCurrent().User?.Value == sid) { }
        return Path.Combine(root, "rules.db");
    }

    internal static void ProtectFile(string path, string sid)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Rule database file is unavailable or redirected.");
        if ((file.Attributes & FileAttributes.Encrypted) != 0) File.Decrypt(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        try { file.SetAccessControl(security); }
        catch (UnauthorizedAccessException) when (WindowsIdentity.GetCurrent().User?.Value == sid) { }
    }
}
