// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NDM.App;

public partial class DownloadProgressView
{
    /// <summary>2.0.0: fills the Server row (flag, country or proxy, answer time with its colour word); the row is hidden when nothing is known.</summary>
    private void RefreshServer()
    {
        var view = Row.Server;
        var show = view is not null;
        ServerLabel.Visibility = ServerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (view is null) { ServerText.Inlines.Clear(); ServerFlag.Source = null; return; }
        ServerFlag.Source = FlagImages.Get(view.Code); ServerFlag.Visibility = ServerFlag.Source is null ? Visibility.Collapsed : Visibility.Visible;
        ServerText.Inlines.Clear();
        ServerText.Inlines.Add(new Run(view.Lead));
        if (view.Latency is not null)
        {
            ServerText.Inlines.Add(new Run(", "));
            var run = new Run(view.Latency); run.SetResourceReference(TextElement.ForegroundProperty, view.BrushKey); ServerText.Inlines.Add(run);
        }
        ServerPanel.ToolTip = view.Host + " - " + Localization.Loc.T("server.tip");
    }
}
