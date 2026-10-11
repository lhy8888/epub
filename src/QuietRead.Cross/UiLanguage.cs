using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Data;
using Avalonia.Data.Converters;
using QuietRead.Core;

namespace QuietRead.Cross;

public sealed class UiLanguage(string language) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Language { get; private set; } = InterfaceText.NormalizeLanguage(language);
    public string Get(string key, params object?[] args) => InterfaceText.Get(Language, key, args);
    public void Set(string language)
    {
        language = InterfaceText.NormalizeLanguage(language);
        if (language == Language) return;
        Language = language; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }
    public Binding Binding(string key, params object?[] args) => new(nameof(Language))
    { Source = this, Converter = new MessageConverter(key, args), Mode = BindingMode.OneWay };
    public void Bind(Control target, AvaloniaProperty property, string key, params object?[] args)
    {
        target.Bind(property, Binding(key, args));
        if (target is Button) target.Bind(AutomationProperties.NameProperty, Binding(key, args));
    }
    private sealed class MessageConverter(string key, object?[] args) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => InterfaceText.Get(value as string, key, args);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
