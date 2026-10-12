namespace SuperCursor.Services;

public class SuperCursorTool(EntitySelectionService entitySelectionService, InputService inputService, IOptionsBox optionsBox, UIVisibilityManager uiVisibilityManager, SelectableObjectRaycaster selectableObjectRaycaster,
    SuperCursorText label,
    IEnumerable<IObjectDescriber> objDescribers,
    CursorCoordinatesPicker cursorCoords,
    IEnumerable<ICoordDescriber> coordDescribers,
    ILoc loc,
    EventBus eb
) : CursorTool(entitySelectionService, inputService, optionsBox, uiVisibilityManager, selectableObjectRaycaster), IInputProcessor, ILoadableSingleton
{
    const string Keybinding = "SuperCursor";
    readonly SelectableObjectRaycaster selectableObjectRaycaster = selectableObjectRaycaster;
    readonly InputService inputService = inputService;

    // Only the expensive tooltip contents are rate-limited. Cursor movement,
    // visibility and input processing continue at the game's normal cadence.
    const double NormalInfoRefreshMs = 50.0;
    const double ModerateInfoRefreshMs = 100.0;
    const double SlowInfoRefreshMs = 250.0;
    long nextInfoRefreshTimestamp;

    static readonly Vector2 MouseMargin = new(10, 10);

    public void Load()
    {
        eb.Register(this);
    }

    void ProcessInfo()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now >= nextInfoRefreshTimestamp)
        {
            RefreshInfo(now);
        }

        // The label must follow the mouse every frame, even when its text is
        // deliberately reused. Do not throttle tool input or hover placement.
        MoveLabel();
        label.Visible = true;
    }

    void RefreshInfo(long startedAt)
    {
        StringBuilder info = new();

        ProcessCoords(info);
        ProcessObject(info);

        if (info.Length == 0)
        {
            info.Append(loc.T("LV.SC.Nothing"));
        }

        label.Text = info.ToString();

        var finishedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedMs = (finishedAt - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var refreshMs = elapsedMs >= 100.0
            ? SlowInfoRefreshMs
            : elapsedMs >= 30.0
                ? ModerateInfoRefreshMs
                : NormalInfoRefreshMs;

        // Measure from completion, avoiding continuous refreshes when an
        // unusually expensive sample exceeds the nominal refresh interval.
        nextInfoRefreshTimestamp = finishedAt +
            (long)(refreshMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
    }

    void MoveLabel()
    {
        var mousePos = _inputService.MousePosition;

        var x = mousePos.x + MouseMargin.x;
        // y is upside-down between the game and the UI
        var y = Screen.height - mousePos.y + MouseMargin.y;

        var scale = label.panel.scaledPixelsPerPoint;

        var scaledX = x / scale;
        var scaledY = y / scale;

        scaledX = Math.Min(scaledX, Screen.width / scale - label.layout.width);
        scaledY = Math.Min(scaledY, Screen.height / scale - label.layout.height);

        label.MoveTo(scaledX, scaledY);
    }

    void ProcessObject(StringBuilder builder)
    {
        if (!selectableObjectRaycaster.TryHitSelectableObject(out var obj)) { return; }

        foreach (var desc in objDescribers)
        {
            desc.Describe(builder, obj);
        }
    }

    void ProcessCoords(StringBuilder builder)
    {
        var coords = cursorCoords.Pick();
        if (coords is null) { return; }

        foreach (var desc in coordDescribers)
        {
            desc.Describe(builder, coords.Value);
        }
    }

    [OnEvent]
    public void OnToolExited(ToolExitedEvent e)
    {
        if (e.Tool == this)
        {
            label.Visible = false;
            nextInfoRefreshTimestamp = 0;
        }
    }

    bool IInputProcessor.ProcessInput()
    {
        var result = ProcessInput();

        if (!inputService.MouseOverUI && inputService.IsKeyHeld(Keybinding))
        {
            ProcessInfo();
        }
        else
        {
            label.Visible = false;
            // Show fresh information immediately when Shift is held again or
            // the pointer returns from UI, not a possibly stale cached value.
            nextInfoRefreshTimestamp = 0;
        }

        return result;
    }

}
