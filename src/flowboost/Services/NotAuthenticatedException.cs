namespace flowboost.Services;

public sealed class NotAuthenticatedException : InvalidOperationException
{
    public NotAuthenticatedException() : base("Sign in to GitHub Copilot to use this preset.") { }
}
