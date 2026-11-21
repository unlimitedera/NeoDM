// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;

namespace NDM.App;

/// <summary>Small themed single-line input; <paramref name="validate"/> returns an error message or null.</summary>
public partial class TextPromptWindow : Window
{
    private readonly Func<string, string?> _validate;
    public string Value { get; private set; } = "";
    public TextPromptWindow(string title, string prompt, string initial, Func<string, string?> validate)
    {
        _validate = validate; InitializeComponent();
        Title = title; Prompt.Text = prompt; Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }
    public void Accept()
    {
        var text = Input.Text.Trim();
        if (_validate(text) is { } problem) { ErrorText.Text = problem; return; }
        Value = text;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();
    private void Cancel_Click(object sender, RoutedEventArgs e) { try { DialogResult = false; } catch (InvalidOperationException) { Close(); } }
}
