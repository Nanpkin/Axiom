namespace Axiom.Engine;

/// <summary>Editorial configuration only. These records never enter the executable rule index.</summary>
public sealed record RuleDraft(string Id, long Version, string Title, string ConditionText,
    string ActionText, DateTimeOffset UpdatedAtUtc);

public sealed record RuleDraftEdit(string Id, long ExpectedVersion, string Title,
    string ConditionText, string ActionText);

public enum RuleDraftSaveStatus { Saved, Conflict }

public sealed record RuleDraftSaveResult(RuleDraftSaveStatus Status, RuleDraft? Draft, long? CurrentVersion);

public interface IRuleDraftStore
{
    Task<IReadOnlyList<RuleDraft>> ListAsync(CancellationToken cancellationToken);
    Task<RuleDraftSaveResult> SaveAsync(RuleDraftEdit edit, DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}

public sealed partial class EngineHost
{
    private IRuleDraftStore? draftStore;
    private string? draftOwnerSid;

    public void ConfigureRuleDrafts(IRuleDraftStore store, string ownerSid)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(ownerSid)) throw new ArgumentException("Owner SID is required.", nameof(ownerSid));
        lock (gate)
        {
            RequireRunning();
            if (draftStore is not null) throw new InvalidOperationException("Rule draft store is already configured.");
            draftStore = store;
            draftOwnerSid = ownerSid;
        }
    }

    public Task<IReadOnlyList<RuleDraft>> ListRuleDraftsAsync(string requesterSid,
        CancellationToken cancellationToken = default)
    {
        IRuleDraftStore store;
        lock (gate)
        {
            RequireRunning();
            if (!string.Equals(requesterSid, draftOwnerSid, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Rule drafts belong to another user.");
            store = draftStore ?? throw new InvalidOperationException("Rule draft store is not configured.");
        }
        return store.ListAsync(cancellationToken);
    }

    public Task<RuleDraftSaveResult> SaveRuleDraftAsync(string requesterSid, RuleDraftEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        IRuleDraftStore store;
        DateTimeOffset now;
        lock (gate)
        {
            RequireRunning();
            if (!string.Equals(requesterSid, draftOwnerSid, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Rule drafts belong to another user.");
            store = draftStore ?? throw new InvalidOperationException("Rule draft store is not configured.");
            now = clock.UtcNow;
        }
        if (!Guid.TryParseExact(edit.Id, "D", out _) || edit.ExpectedVersion < 0 || edit.ExpectedVersion == long.MaxValue)
            throw new ArgumentException("Invalid rule draft identity or version.");
        ValidateText(edit.Title, 120, nameof(edit.Title));
        ValidateText(edit.ConditionText, 2000, nameof(edit.ConditionText));
        ValidateText(edit.ActionText, 2000, nameof(edit.ActionText));
        // The store contract returns Saved only after SQLite has committed.
        return store.SaveAsync(edit with
        {
            Title = edit.Title.Trim(),
            ConditionText = edit.ConditionText.Trim(),
            ActionText = edit.ActionText.Trim()
        }, now, cancellationToken);
    }

    private static void ValidateText(string? text, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            throw new ArgumentException($"Invalid {field}.");
    }
}
