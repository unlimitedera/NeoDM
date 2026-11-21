// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0: the plain-words messages of the Refresh link action, one place for the progress window, the tab, the list menu and the tests.</summary>
public static class RefreshTexts
{
    public static string Stage(RefreshStage stage) => Loc.T(stage switch { RefreshStage.SignIn => "refresh.stage.signin", RefreshStage.Reading => "refresh.stage.reading", _ => "refresh.stage.opening" });
    public static string Outcome(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.Refreshed => Loc.T("refresh.done"),
        RefreshOutcome.NoPage => Loc.T("refresh.fail.nopage"),
        RefreshOutcome.NoExtension => Loc.T("refresh.fail.noextension"),
        RefreshOutcome.NeedsSignIn => Loc.T("refresh.fail.signin"),
        RefreshOutcome.NotFound => Loc.T("refresh.fail.notfound"),
        _ => "",
    };
}
