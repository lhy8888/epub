using System.Windows;
using System.Windows.Controls;
using QuietRead.Core;

namespace QuietRead;

public partial class MainWindow
{
    private void InterfaceLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_closed && LanguageCombo.SelectedItem is ComboBoxItem item)
            ChangeInterfaceLanguage((string)item.Tag);
    }

    internal void ChangeInterfaceLanguage(string language)
    {
        if (_closed) return;
        language = InterfaceText.NormalizeLanguage(language);
        if (_state.InterfaceLanguage == language) return;
        _state.InterfaceLanguage = language;
        UiText.Current.Language = language;
        LanguageCombo.SelectedIndex = language == "en" ? 1 : 0;
        ScheduleSave();
    }
}
