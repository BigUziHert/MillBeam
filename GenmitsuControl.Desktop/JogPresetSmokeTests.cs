using System.Globalization;
using System.Windows.Controls;

namespace GenmitsuControl.Desktop;

public partial class MainWindow
{
    private async Task RunJogPresetSmokeTestsAsync(Action<bool, string> assert, Action<Button> click,
        Func<Task> waitReady, Func<Func<bool>, string, Task> waitUntil)
    {
        var inputs = UnitInputs;
        double[] metricSteps = { 1, 2, 3, 6, 13, 25, 50 }, inchSteps = { .03125, .0625, .125, .25, .5, 1, 2 };
        double[] metricFeeds = { 125, 300, 500, 1000, 1500, 2000 }, inchFeeds = { 5, 12, 20, 40, 60, 78 };
        double[] customValues = { 4.56789123, 456.78912345, .23456789, 123.45678912 };
        string Number(double value) => value.ToString("0.########", CultureInfo.CurrentCulture);
        double Read(int index) => ReadMillimeters(inputs[index], "preset check", index % 2 == 0 ? 100 : 10000);
        TextBox Editor(JogPresetPicker picker) => (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
        void Type(JogPresetPicker picker, string text)
        {
            var editor = Editor(picker);
            editor.Focus();
            editor.SelectAll();
            editor.SelectedText = text;
            assert(picker.PresetIndex is null && picker.SelectedIndex == -1 && picker.Text == text,
                $"{picker.Name}: editing the actual text box preserves custom text and clears preset intent");
        }
        void Select(JogPresetPicker picker, int index)
        {
            picker.IsDropDownOpen = true;
            UpdateLayout();
            var item = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(index);
            item.IsSelected = true;
            picker.IsDropDownOpen = false;
            assert(picker.PresetIndex == index && picker.Text == ((UnitPreset)picker.Items[index]).DisplayText,
                $"{picker.Name}: choosing menu row {index + 1} establishes explicit preset intent");
        }

        assert(!_displayInches, "Preset pairing checks begin in millimeters");
        for (int i = 0; i < inputs.Length; i++) Type(inputs[i], Number(customValues[i]));
        int logStart = _log.Length;
        var settingsBefore = Controller.Snapshot.Settings.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (int control = 0; control < inputs.Length; control++)
        {
            var metric = control % 2 == 0 ? metricSteps : metricFeeds;
            var inches = control % 2 == 0 ? inchSteps : inchFeeds;
            for (int preset = 0; preset < metric.Length; preset++)
            {
                Select(inputs[control], preset);
                click(UnitsButton);
                assert(_displayInches && inputs[control].PresetIndex == preset
                    && inputs[control].Text == ((UnitPreset)inputs[control].Items[preset]).DisplayText
                    && Read(control) == inches[preset] * 25.4,
                    $"{inputs[control].Name}: metric preset {preset + 1} pairs to its actual native inch value");
                assert(inputs.Where((_, index) => index != control).All(picker => picker.PresetIndex is null)
                    && Enumerable.Range(0, inputs.Length).Where(index => index != control)
                        .All(index => Math.Abs(Read(index) - customValues[index]) < 1e-10),
                    $"{inputs[control].Name}: pairing leaves the other three custom inputs independent");
                click(UnitsButton);
                assert(!_displayInches && inputs[control].PresetIndex == preset && Read(control) == metric[preset],
                    $"{inputs[control].Name}: inch preset {preset + 1} pairs back to its actual native metric value");
            }
            Type(inputs[control], Number(customValues[control]));
        }

        // Replacing selected text with the very same number must still be custom,
        // and choosing that same menu row afterward must restore preset behavior.
        foreach (bool inchesMode in new[] { false, true })
        {
            if (_displayInches != inchesMode) click(UnitsButton);
            for (int control = 0; control < inputs.Length; control++)
            {
                int preset = control % 2 == 0 ? 0 : 2;
                double native = control % 2 == 0
                    ? (inchesMode ? inchSteps : metricSteps)[preset]
                    : (inchesMode ? inchFeeds : metricFeeds)[preset];
                Select(inputs[control], preset);
                Type(inputs[control], Number(native));
                double physical = native * (inchesMode ? 25.4 : 1);
                for (int toggle = 0; toggle < 4; toggle++)
                {
                    click(UnitsButton);
                    assert(inputs[control].PresetIndex is null && Math.Abs(Read(control) - physical) < 1e-12,
                        $"{inputs[control].Name}: typed preset-equivalent value stays custom without physical drift");
                }
                Select(inputs[control], preset);
                click(UnitsButton);
                double paired = control % 2 == 0
                    ? (_displayInches ? inchSteps[preset] * 25.4 : metricSteps[preset])
                    : (_displayInches ? inchFeeds[preset] * 25.4 : metricFeeds[preset]);
                assert(inputs[control].PresetIndex == preset && Read(control) == paired,
                    $"{inputs[control].Name}: reselecting the former row restores true paired-preset behavior");
                click(UnitsButton);
            }
        }
        if (_displayInches) click(UnitsButton);
        Select(FeedPicker, 2);
        Type(FeedPicker, "777");
        Editor(FeedPicker).Undo();
        assert(FeedPicker.Text == "500" && FeedPicker.PresetIndex is null,
            "Undo restores the previous numeric text as custom input without reselecting a preset");
        Editor(FeedPicker).Redo();
        assert(FeedPicker.Text == "777" && FeedPicker.PresetIndex is null,
            "Redo restores the edited custom number");

        Select(DistancePicker, 0); Select(FeedPicker, 2); Select(ZFeedPicker, 0);
        Type(ZDistancePicker, "invalid");
        var beforeInvalid = inputs.Select(picker => (picker.Text, picker.PresetIndex)).ToArray();
        click(UnitsButton);
        assert(!_displayInches && _localError is not null
            && inputs.Select(picker => (picker.Text, picker.PresetIndex)).SequenceEqual(beforeInvalid),
            "Invalid custom input blocks the entire unit change without partially pairing other controls");
        Type(ZDistancePicker, "1");
        click(UnitsButton);
        assert(_localError is null, "Correcting custom input permits preset pairing again");
        click(UnitsButton);
        assert(!_log.ToString()[logStart..].Contains("> ")
            && Controller.Snapshot.Settings.All(pair => settingsBefore[pair.Key] == pair.Value),
            "Preset and custom unit changes issue no motion or controller-setting commands");

        var positiveX = JogPad.Children.OfType<Button>().Single(button => (string)button.Tag == "X+");
        var positiveZ = ZJogPad.Children.OfType<Button>().Single(button => (string)button.Tag == "Z+");
        async Task CheckJog(Button button, string command, double x, double z)
        {
            var start = Controller.Snapshot.MachinePosition!;
            int startLog = _log.Length;
            click(button); await waitReady();
            await waitUntil(() => Controller.Snapshot.CanMutate, "Preset/custom simulator jog completes");
            var end = Controller.Snapshot.MachinePosition!;
            assert(_log.ToString()[startLog..].Contains(command)
                && Math.Abs(end.X - start.X - x) < .003 && Math.Abs(end.Y - start.Y) < .003
                && Math.Abs(end.Z - start.Z - z) < .003,
                $"Displayed preset/custom inputs produce the actual simulated distance and feed: {command}");
        }
        Select(DistancePicker, 1); Select(FeedPicker, 2); Select(ZDistancePicker, 3); Select(ZFeedPicker, 0);
        click(UnitsButton);
        await CheckJog(positiveX, "$J=G91 G21 X1.5875 F508", 1.5875, 0);
        await CheckJog(positiveZ, "$J=G91 G21 Z6.35 F127", 0, 6.35);
        click(UnitsButton);
        await CheckJog(positiveX, "$J=G91 G21 X2 F500", 2, 0);
        await CheckJog(positiveZ, "$J=G91 G21 Z6 F125", 0, 6);
        Type(DistancePicker, "1"); Type(FeedPicker, "500"); Type(ZDistancePicker, "2"); Type(ZFeedPicker, "300");
        click(UnitsButton);
        await CheckJog(positiveX, "$J=G91 G21 X1 F500", 1, 0);
        await CheckJog(positiveZ, "$J=G91 G21 Z2 F300", 0, 2);
        click(UnitsButton);
    }
}
