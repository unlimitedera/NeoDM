using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xaml;

namespace NDM.App.Localization;

/// <summary>XAML: <c>Header="{loc:T main.menu.tasks}"</c>. A one-way binding to the localisation source, so the text changes when the language changes.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TExtension() { }
    public TExtension(string key) { Key = key; }
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        WindowLocalization.OnParsing((serviceProvider.GetService(typeof(IRootObjectProvider)) as IRootObjectProvider)?.RootObject);
        var binding = new Binding("[" + Key + "]") { Source = Loc.Source, Mode = BindingMode.OneWay };
        // Setters and templates take the binding object itself; ordinary properties take its value expression.
        return binding.ProvideValue(serviceProvider);
    }
}
