using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>One row of the category editor in Options > Save To (a built-in or a custom category). Built-in folders live in the five old text boxes; the row reads and writes them.</summary>
public sealed class CategoryEdit : INotifyPropertyChanged
{
    public string BuiltInId { get; init; } = "";
    public bool IsBuiltIn => BuiltInId.Length > 0;
    private string _name = "", _folder = "", _extensions = "", _rules = ""; private bool _enabled = true;
    public Func<string>? FolderGet { get; init; }
    public Action<string>? FolderSet { get; init; }
    public string Name { get => IsBuiltIn ? Loc.T("cat.builtin." + BuiltInId.ToLowerInvariant()) : _name; set { _name = value; Raise(nameof(Name)); Raise(nameof(Display)); } }
    public string Folder { get => FolderGet is not null ? FolderGet() : _folder; set { if (FolderSet is not null) FolderSet(value); else _folder = value; Raise(nameof(Folder)); Raise(nameof(Display)); } }
    public string Extensions { get => _extensions; set { _extensions = value; Raise(nameof(Extensions)); } }
    public string Rules { get => _rules; set { _rules = value; Raise(nameof(Rules)); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); Raise(nameof(Display)); } }
    public string Display => Name + (IsBuiltIn ? "  \u00B7  " + Loc.T("cat.badge.builtin") : "") + (Enabled ? "" : "  \u00B7  " + Loc.T("cat.badge.off"));
    public string Tip => Loc.T("cat.row_tip", Name, string.IsNullOrWhiteSpace(Folder) ? "." : Folder);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { Raise(nameof(Name)); Raise(nameof(Display)); Raise(nameof(Tip)); }
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class SettingsWindow
{
    private bool _loadingCategory;
    private System.Collections.ObjectModel.ObservableCollection<CategoryEdit> _categoryItems = new();

    private TextBox BuiltInBox(string id) => id switch { "Programs" => ProgramsFolder, "Compressed" => CompressedFolder, "Music" => MusicFolder, "Video" => VideoFolder, _ => DocumentsFolder };

    private void InitCategoryEditor(AppSettings settings)
    {
        _categoryItems = new();
        foreach (var c in settings.CustomCategories)
            _categoryItems.Add(new CategoryEdit { Name = c.Name, Folder = c.Folder, Extensions = CategoryRules.FormatExtensions(c.Extensions), Rules = string.Join(Environment.NewLine, c.UrlRules), Enabled = c.Enabled });
        foreach (var id in CategoryRules.BuiltInIds)
        {
            var box = BuiltInBox(id);
            var item = new CategoryEdit
            {
                BuiltInId = id, FolderGet = () => box.Text, FolderSet = value => { if (box.Text != value) box.Text = value; },
                Extensions = CategoryRules.FormatExtensions(CategoryRules.BuiltInExtensions(settings, id)), Rules = string.Join(Environment.NewLine, CategoryRules.BuiltInRules(settings, id)), Enabled = CategoryRules.BuiltInEnabled(settings, id)
            };
            box.TextChanged += (_, _) => item.Refresh();
            _categoryItems.Add(item);
        }
        CategoryList.ItemsSource = _categoryItems;
        var presets = new List<ComboBoxItem> { new() { Content = Loc.T("cat.preset.add"), Tag = null } };
        foreach (var (key, list) in CategoryRules.Presets) presets.Add(new ComboBoxItem { Content = Loc.T(key), Tag = list });
        CategoryPreset.ItemsSource = presets; CategoryPreset.SelectedIndex = 0;
        CategoryList.SelectedIndex = _categoryItems.Count > 0 ? 0 : -1;
        UpdateCategoryButtons();
    }

    private CategoryEdit? CurrentCategory => CategoryList.SelectedItem as CategoryEdit;

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loadingCategory = true;
        try
        {
            var c = CurrentCategory;
            CategoryEditor.IsEnabled = c is not null;
            CategoryName.Text = c?.Name ?? ""; CategoryName.IsReadOnly = c?.IsBuiltIn == true;
            CategoryFolder.Text = c?.Folder ?? ""; CategoryExtensions.Text = c?.Extensions ?? ""; CategoryRulesBox.Text = c?.Rules ?? ""; CategoryEnabled.IsChecked = c?.Enabled ?? true;
        }
        finally { _loadingCategory = false; }
        UpdateCategoryButtons(); ShowCategoryProblem();
    }

    private void UpdateCategoryButtons()
    {
        var c = CurrentCategory; int index = c is null ? -1 : _categoryItems.IndexOf(c); int customCount = _categoryItems.Count(i => !i.IsBuiltIn);
        CategoryRemoveButton.IsEnabled = c is { IsBuiltIn: false };
        CategoryUpButton.IsEnabled = c is { IsBuiltIn: false } && index > 0;
        CategoryDownButton.IsEnabled = c is { IsBuiltIn: false } && index < customCount - 1;
        CategoryAddButton.IsEnabled = customCount < CategoryRules.MaxCustom;
    }

    private void CategoryField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCategory || CurrentCategory is not { } c) return;
        if (!c.IsBuiltIn) c.Name = CategoryName.Text;
        c.Folder = CategoryFolder.Text; c.Extensions = CategoryExtensions.Text; c.Rules = CategoryRulesBox.Text; c.Enabled = CategoryEnabled.IsChecked == true;
        ShowCategoryProblem();
    }

    private void CategoryAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryItems.Count(i => !i.IsBuiltIn) >= CategoryRules.MaxCustom) return;
        string baseName = Loc.T("cat.new_name"); string name = baseName; int n = 2;
        while (_categoryItems.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) name = baseName + " " + n++;
        var item = new CategoryEdit { Name = name, Folder = name };
        int at = _categoryItems.Count(i => !i.IsBuiltIn);
        _categoryItems.Insert(at, item); CategoryList.SelectedItem = item; CategoryList.ScrollIntoView(item);
        CategoryName.Focus(); CategoryName.SelectAll();
    }

    private void CategoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCategory is not { IsBuiltIn: false } c) return;
        int index = _categoryItems.IndexOf(c); _categoryItems.Remove(c);
        int customCount = _categoryItems.Count(i => !i.IsBuiltIn);
        CategoryList.SelectedIndex = Math.Min(index, Math.Max(0, customCount - 1)); if (customCount == 0) CategoryList.SelectedIndex = 0;
        CategoryList.Focus();
    }

    private void CategoryMove(int delta)
    {
        if (CurrentCategory is not { IsBuiltIn: false } c) return;
        int index = _categoryItems.IndexOf(c), target = index + delta, customCount = _categoryItems.Count(i => !i.IsBuiltIn);
        if (target < 0 || target >= customCount) return;
        _categoryItems.Move(index, target); CategoryList.SelectedItem = c; UpdateCategoryButtons();
    }
    private void CategoryUp_Click(object sender, RoutedEventArgs e) => CategoryMove(-1);
    private void CategoryDown_Click(object sender, RoutedEventArgs e) => CategoryMove(1);

    private void CategoryReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _categoryItems.Where(i => i.IsBuiltIn))
        {
            item.Folder = item.BuiltInId; item.Extensions = CategoryRules.FormatExtensions(CategoryRules.DefaultExtensions(item.BuiltInId)); item.Rules = ""; item.Enabled = true;
        }
        CategoryList_SelectionChanged(CategoryList, null!);
    }

    private void CategoryBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.T("cat.picker"), Multiselect = false };
        var current = CategoryFolder.Text.Trim();
        try { if (Path.IsPathFullyQualified(current) && Directory.Exists(current)) picker.InitialDirectory = current; } catch (ArgumentException) { }
        if (picker.ShowDialog(this) == true) CategoryFolder.Text = picker.FolderName;
    }

    private void CategoryPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCategory || CategoryPreset.SelectedItem is not ComboBoxItem { Tag: string[] list }) return;
        var merged = CategoryRules.ParseExtensions(CategoryExtensions.Text); foreach (var x in list) if (!merged.Contains(x)) merged.Add(x);
        CategoryExtensions.Text = CategoryRules.FormatExtensions(merged);
        CategoryPreset.SelectedIndex = 0;
    }

    /// <summary>Builds the settings from the editor rows.</summary>
    private void ApplyCategoryEditor(AppSettings updated)
    {
        updated.CustomCategories = _categoryItems.Where(i => !i.IsBuiltIn).Select(i => new CustomCategory
        {
            Name = i.Name.Trim(), Folder = i.Folder.Trim(), Extensions = CategoryRules.ParseExtensions(i.Extensions), UrlRules = CategoryRules.ParseRules(i.Rules), Enabled = i.Enabled
        }).ToList();
        updated.BuiltInCategories = new();
        foreach (var i in _categoryItems.Where(i => i.IsBuiltIn))
        {
            var parsed = CategoryRules.ParseExtensions(i.Extensions); var defaults = CategoryRules.DefaultExtensions(i.BuiltInId);
            bool same = parsed.Count == defaults.Count && parsed.All(x => defaults.Contains(x));
            updated.BuiltInCategories[i.BuiltInId] = new BuiltInCategorySetting { Extensions = same ? null : parsed, UrlRules = CategoryRules.ParseRules(i.Rules), Enabled = i.Enabled };
        }
        // Keep what the user typed for the built-in folders; Normalize drops entries that equal the defaults.
        var typedCustom = updated.CustomCategories.Count; var typedNames = updated.CustomCategories.Select(c => c.Name).ToList();
        var problem = CategoryRules.Validate(updated);
        if (problem is { } p) throw new UserMessageException(CategoryProblemText(p.Key, p.Arg));
        CategoryRules.Normalize(updated);
        if (updated.CustomCategories.Count != typedCustom) throw new UserMessageException(CategoryProblemText("cat.err.invalid", null));
    }

    private static string CategoryProblemText(string key, string? arg)
    {
        if (arg is null) return Loc.T(key);
        var display = CategoryRules.BuiltInIds.Contains(arg) ? Loc.T("cat.builtin." + arg.ToLowerInvariant()) : arg;
        return key is "cat.err.name_long" or "cat.err.name_taken" or "cat.err.no_rule" ? Loc.T(key, display) : Loc.T("cat.err.in", display, Loc.T(key));
    }

    /// <summary>The first problem with the selected row, shown under the editor; empty when it is fine.</summary>
    private void ShowCategoryProblem()
    {
        string text = "";
        if (CurrentCategory is { } c)
        {
            var typedName = c.Name.Trim();
            if (!c.IsBuiltIn)
            {
                if (typedName.Length == 0) text = Loc.T("cat.err.name_empty");
                else if (typedName.Length > CategoryRules.MaxName) text = Loc.T("cat.err.name_long", typedName);
                else if (CategoryRules.BuiltInIds.Contains(typedName, StringComparer.OrdinalIgnoreCase) || _categoryItems.Any(o => o != c && !o.IsBuiltIn && string.Equals(o.Name.Trim(), typedName, StringComparison.OrdinalIgnoreCase))) text = Loc.T("cat.err.name_taken", typedName);
            }
            if (text.Length == 0 && CategoryPaths.Resolve(Destination.Text.Trim().Length > 0 && Path.IsPathFullyQualified(Destination.Text.Trim()) ? Destination.Text.Trim() : _settings.DownloadDirectory, c.Folder, c.IsBuiltIn, out var err) is null) text = Loc.T(err!);
            if (text.Length == 0) foreach (var r in CategoryRules.ParseRules(c.Rules)) if (UrlRuleMatcher.Validate(r) is { } rk) { text = Loc.T(rk); break; }
            if (text.Length == 0 && !c.IsBuiltIn && CategoryRules.ParseExtensions(c.Extensions).Count == 0 && CategoryRules.ParseRules(c.Rules).Count == 0) text = Loc.T("cat.err.no_rule", typedName);
        }
        CategoryMessage.Text = text;
        CategoryMessage.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshCategoryLanguage()
    {
        if (_categoryItems is null) return;
        foreach (var i in _categoryItems) i.Refresh();
        if (CategoryPreset?.ItemsSource is List<ComboBoxItem> presets && presets.Count == CategoryRules.Presets.Length + 1)
        { presets[0].Content = Loc.T("cat.preset.add"); for (int k = 0; k < CategoryRules.Presets.Length; k++) presets[k + 1].Content = Loc.T(CategoryRules.Presets[k].Key); }
        if (!_loadingCategory && CategoryName is not null && CurrentCategory is { IsBuiltIn: true } c) CategoryName.Text = c.Name;
    }
}
