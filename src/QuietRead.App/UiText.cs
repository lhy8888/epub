using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using QuietRead.Core;

namespace QuietRead;

internal sealed class UiText : INotifyPropertyChanged
{
    internal static UiText Current { get; } = new();
    private string _language = "zh-CN";
    public string Language
    {
        get => _language;
        set
        {
            string language = InterfaceText.NormalizeLanguage(value);
            if (_language == language) return;
            _language = language;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal sealed record Message(string Source, object?[] Arguments);
    private sealed record ErrorMessage(string Source);
    internal static Message Part(string source, params object?[] arguments) => new(source, arguments);
    internal static object Error(string message) => new ErrorMessage(message);
    internal static string Get(string source, params object?[] arguments) => Resolve(Current.Language, Part(source, arguments));
    internal static string GetError(string message) => InterfaceText.Error(Current.Language, message);
    internal static void Text(TextBlock target, string source, params object?[] arguments) => Bind(target, TextBlock.TextProperty, source, arguments);
    internal static void Bind(DependencyObject target, DependencyProperty property, string source, params object?[] arguments) =>
        BindingOperations.SetBinding(target, property, Binding(source, arguments));
    internal static Binding Binding(string source, params object?[] arguments) => new(nameof(Language))
    { Source = Current, Mode = BindingMode.OneWay, Converter = new MessageConverter(Part(source, arguments)) };

    private static string Resolve(string language, Message message) => message.Arguments.Length == 0 ? InterfaceText.Get(language, message.Source) :
        InterfaceText.Get(language, message.Source, message.Arguments.Select(value => value switch
        {
            Message nested => Resolve(language, nested),
            ErrorMessage error => InterfaceText.Error(language, error.Source),
            _ => value
        }).ToArray());

    private sealed class MessageConverter(Message message) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Resolve((string)value, message);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Binding(Key).ProvideValue(serviceProvider);
}
