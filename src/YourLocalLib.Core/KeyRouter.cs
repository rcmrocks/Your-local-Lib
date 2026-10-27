namespace YourLocalLib.Core;

public enum KeyDecision
{
    None,
    UndoScan,
    OpenSearch,
    ClearBuffer,
    DismissPrompt,
    AcceptPrompt,
    DeclinePrompt
}

public static class KeyRouter
{
    /// <param name="typing">A text box that keeps Ctrl+Z for its own typing has focus.</param>
    /// <param name="onScanScreen">Scan keys act only on the Scan screen. Ctrl+F works everywhere.</param>
    public static KeyDecision Decide(string key, bool control, bool typing, bool promptOpen, bool onScanScreen = true)
    {
        var name = key.Trim().ToUpperInvariant();
        if (control && name is "F")
            return KeyDecision.OpenSearch;
        if (!onScanScreen)
            return KeyDecision.None;
        if (control && name is "Z")
            return typing ? KeyDecision.None : KeyDecision.UndoScan;
        if (name is "ESCAPE" or "ESC")
            return promptOpen ? KeyDecision.DismissPrompt : KeyDecision.ClearBuffer;
        if (!promptOpen)
            return KeyDecision.None;
        if (name is "Y")
            return KeyDecision.AcceptPrompt;
        if (name is "N")
            return KeyDecision.DeclinePrompt;
        return KeyDecision.None;
    }
}
