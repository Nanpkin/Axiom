using System.Diagnostics;
using System.Globalization;

namespace Axiom.Contracts;

// Endpoints are session-local even though Windows named pipe names are machine-wide.
// Routing does not replace the existing OS user, session and peer-role checks.
internal static class SessionPipeName
{
    private static readonly int CurrentSession = ReadCurrentSession();

    public static string Create(string prefix, string sid, int? sessionId)
    {
        var session = sessionId ?? CurrentSession;
        ArgumentOutOfRangeException.ThrowIfNegative(session);
        return prefix + sid.Replace('-', '_') + "-s" + session.ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadCurrentSession()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }
}
