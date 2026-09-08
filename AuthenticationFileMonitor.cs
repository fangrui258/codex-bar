namespace CodexBar;

internal sealed class AuthenticationFileMonitor
{
    private readonly string path;
    private AuthenticationFileState lastState;
    private bool hasObservation;

    public AuthenticationFileMonitor(string path)
    {
        this.path = path;
    }

    public static AuthenticationFileMonitor CreateDefault()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return new AuthenticationFileMonitor(Path.Combine(codexHome, "auth.json"));
    }

    public bool HasChanged()
    {
        if (!TryReadState(out var currentState)) return false;

        if (!hasObservation)
        {
            lastState = currentState;
            hasObservation = true;
            return false;
        }

        if (currentState == lastState) return false;
        lastState = currentState;
        return true;
    }

    private bool TryReadState(out AuthenticationFileState state)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            state = file.Exists
                ? new AuthenticationFileState(true, file.Length, file.LastWriteTimeUtc.Ticks)
                : new AuthenticationFileState(false, 0, 0);
            return true;
        }
        catch (IOException)
        {
            state = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            state = default;
            return false;
        }
    }

    private readonly record struct AuthenticationFileState(
        bool Exists,
        long Length,
        long LastWriteTimeUtcTicks);
}
