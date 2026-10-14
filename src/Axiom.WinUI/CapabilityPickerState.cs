using Axiom.Domain;

namespace Axiom.WinUI;

// A dialog-only draft. Filtering and selecting never mutate the task editor.
internal sealed class CapabilityPickerState
{
    private readonly TaskCapability[] candidates;
    public string[] Categories { get; }
    public TaskCapability[] Visible { get; private set; }
    public TaskCapability? Selected { get; private set; }

    public CapabilityPickerState(IEnumerable<TaskCapability> candidates, string? selectedId)
    {
        this.candidates = candidates.ToArray();
        Categories = this.candidates.Select(c => c.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture).ToArray();
        Visible = this.candidates;
        Selected = Visible.FirstOrDefault(c => c.Id == selectedId);
    }

    public void Filter(string? category, string query)
    {
        query = query.Trim();
        Visible = candidates.Where(c => (category is null || c.Category == category) &&
            (query.Length == 0 || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             c.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (Selected is not null && !Visible.Contains(Selected)) Selected = null;
    }

    public void Select(TaskCapability? capability) => Selected = capability is not null && Visible.Contains(capability) ? capability : null;

    public static TaskCapability[] Candidates(AutomationSnapshot snapshot, TaskEditorState editor, TaskCapabilityKind kind, bool stateOnly = false) =>
        snapshot.Capabilities.Where(c => c.Kind == kind &&
            (kind != TaskCapabilityKind.Action || editor.SupportsAction(c, snapshot.Combinations)) &&
            (!stateOnly || c.IsStateCondition && (editor.Action is null || snapshot.Combinations.Contains(new(c.Id, editor.Action.CapabilityId))))).ToArray();
}
