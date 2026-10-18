using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace NDM.App.Localization;

/// <summary>Applies the active language to windows: flow direction (mirroring), font chain, XML language and Western digits. New windows are handled at creation, open windows on every language change.</summary>
public static class WindowLocalization
{
    public const string FontKey = "AppFontFamily";
    private static bool _hooked;
    private static readonly ConditionalWeakTable<DependencyObject, object> Attached = new();

    /// <summary>Hooks the language change event and the Window Loaded event. Safe to call many times.</summary>
    public static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
        {
            if (s is Window window && ReferenceEquals(e.OriginalSource, window)) Apply(window);
        }));
        Loc.LanguageChanged += RefreshAll;
        ApplyFont();
    }

    /// <summary>Re-applies the language to every open window and the application font.</summary>
    public static void RefreshAll()
    {
        var app = Application.Current;
        if (app is null) return;
        void Run()
        {
            ApplyFont();
            foreach (Window window in app.Windows) Apply(window);
        }
        if (app.Dispatcher.CheckAccess()) Run(); else app.Dispatcher.Invoke(Run);
    }

    public static void ApplyFont()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Resources[FontKey] = new FontFamily(Loc.Info.Fonts);
    }

    /// <summary>Flow direction, language tag and digit shaping of one window. Called by the parser at creation and again on every language change.</summary>
    public static void Apply(FrameworkElement root)
    {
        Attached.GetValue(root, _ => new object());
        var direction = Loc.FlowDirection;
        if (root.FlowDirection != direction) root.FlowDirection = direction;
        // Right-to-left languages keep an English language tag on the window: with an Arabic tag WPF reorders left-to-right runs ("0 B" becomes "B 0") and ignores direction embeddings. Text shaping depends on the script, not on this tag.
        try { root.Language = XmlLanguage.GetLanguage(Loc.IsRtl ? "en-US" : Loc.Info.Culture); } catch (ArgumentException) { }
        NumberSubstitution.SetSubstitution(root, NumberSubstitutionMethod.European);
        NumberSubstitution.SetCultureSource(root, NumberCultureSource.Override);
        NumberSubstitution.SetCultureOverride(root, Loc.Culture);
    }

    /// <summary>Called from the markup extension while a window is being parsed.</summary>
    internal static void OnParsing(object? rootObject)
    {
        EnsureHooked();
        if (rootObject is Window window && !Attached.TryGetValue(window, out _)) Apply(window);
    }
}
