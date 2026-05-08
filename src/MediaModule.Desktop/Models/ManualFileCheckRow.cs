using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MediaModule.Desktop.Models;

public sealed class ManualFileCheckRow : INotifyPropertyChanged
{
    private string _status = "Ожидает";
    private string _message = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string FilePath { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
