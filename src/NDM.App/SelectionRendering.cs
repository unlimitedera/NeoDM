using System.Runtime.CompilerServices;
namespace NDM.App;

/// <summary>
/// WPF paints the selection of a TextBox or PasswordBox with an adorner on top of the text by default. An opaque selection colour then hides the glyphs and
/// SelectionTextBrush is ignored. Turning the adorner off draws the selection behind the text and applies SelectionTextBrush. The switch is read once, so it
/// is set when this assembly loads, before any control exists. The same switch is also written to NeoDM.runtimeconfig.json by the project file.
/// </summary>
public static class SelectionRendering
{
    public const string SwitchName = "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering";
    [ModuleInitializer] internal static void Initialize() => AppContext.SetSwitch(SwitchName, false);
}
