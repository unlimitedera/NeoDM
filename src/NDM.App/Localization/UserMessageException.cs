// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.App.Localization;

/// <summary>A validation problem whose message is already written in the active language. Still a FormatException, so existing catch blocks keep working;
/// <see cref="ErrorMessages"/> shows it unchanged instead of treating it as an English framework message.</summary>
public sealed class UserMessageException : FormatException
{
    public UserMessageException(string message) : base(message) { }
}
