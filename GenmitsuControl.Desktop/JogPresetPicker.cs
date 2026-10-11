using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GenmitsuControl.Desktop;

/// <summary>
/// Keeps an explicit preset selection distinct from custom text, even when
/// the user types the same number as a preset.
/// </summary>
public sealed class JogPresetPicker : ComboBox
{
    private int _updatingInput;

    public int? PresetIndex { get; private set; }
    public event EventHandler? CustomInputChanged;

    public JogPresetPicker()
    {
        IsTextSearchEnabled = false;
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(EditableTextChanged), true);
    }

    public override void OnApplyTemplate()
    {
        _updatingInput++;
        try
        {
            // Applying a template copies the existing Text into its new editor.
            // That synchronization must not invalidate the saved physical value.
            base.OnApplyTemplate();
        }
        finally { _updatingInput--; }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        bool restoring = _updatingInput != 0;
        _updatingInput++;
        try
        {
            if (!restoring) PresetIndex = SelectedIndex >= 0 ? SelectedIndex : null;
            // ComboBox raises SelectionChanged before it updates the editable
            // text. Keep that entire operation separate from a custom edit.
            base.OnSelectionChanged(e);
        }
        finally { _updatingInput--; }
    }

    private void EditableTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingInput != 0 || e.OriginalSource is not TextBox editor
            || !ReferenceEquals(editor, GetTemplateChild("PART_EditableTextBox"))) return;

        PresetIndex = null;
        if (SelectedIndex >= 0)
        {
            string text = editor.Text;
            int selectionStart = editor.SelectionStart;
            int selectionLength = editor.SelectionLength;
            _updatingInput++;
            try
            {
                // Text search is disabled, so editing otherwise leaves an old
                // SelectedItem behind. Clear it so choosing that same row next
                // time is a real selection again, without losing the user's edit.
                SetCurrentValue(SelectedIndexProperty, -1);
                SetCurrentValue(TextProperty, text);
                editor.Select(Math.Min(selectionStart, text.Length),
                    Math.Min(selectionLength, text.Length - Math.Min(selectionStart, text.Length)));
            }
            finally { _updatingInput--; }
        }
        CustomInputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreInput(string text, int? presetIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (presetIndex is int index && (index < 0 || index >= Items.Count))
            throw new ArgumentOutOfRangeException(nameof(presetIndex));

        _updatingInput++;
        try
        {
            // Clear first so restoring an unchanged preset index also reapplies
            // its label through the normal ComboBox selection behavior.
            SetCurrentValue(SelectedIndexProperty, -1);
            if (presetIndex is int selectedIndex)
                SetCurrentValue(SelectedIndexProperty, selectedIndex);
            else
                SetCurrentValue(TextProperty, text);
            PresetIndex = presetIndex;
        }
        finally { _updatingInput--; }
    }
}
