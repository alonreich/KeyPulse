using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace KeyPulse
{
    public enum ActionType
    {
        OpenFolder,
        LaunchProgram,
        BrowseChrome,
        TypeText,
        InsertText
    }

    public class HotkeyEntry : INotifyPropertyChanged
    {
        private string _registrationStatus = "Pending";
        private bool _isEnabled = true;
        private string _keyCombination = string.Empty;
        private ActionType _action;
        private string _target = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool AllowRiskyShortcut { get; set; }

        private bool _isEditing;
        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RowBackground));
            }
        }

        public string KeyCombination
        {
            get => _keyCombination;
            set
            {
                if (_keyCombination == value) return;
                _keyCombination = value;
                OnPropertyChanged();
            }
        } // e.g. "Ctrl+Alt+A"

        public ActionType Action
        {
            get => _action;
            set
            {
                if (_action == value) return;
                _action = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionDisplay));
            }
        }

        public string Target
        {
            get => _target;
            set
            {
                if (_target == value) return;
                _target = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLongTarget));
                OnPropertyChanged(nameof(TargetOverflowHint));
            }
        }

        [JsonIgnore]
        public bool HasLongTarget => !string.IsNullOrWhiteSpace(Target) && Target.Length > 80;

        [JsonIgnore]
        public string TargetOverflowHint => HasLongTarget
            ? "This target is shortened in the list. Select the row to edit the full value above."
            : string.Empty;

        [JsonIgnore]
        public string ActionDisplay => Action switch
        {
            ActionType.OpenFolder => "Open folder",
            ActionType.LaunchProgram => "Launch program",
            ActionType.BrowseChrome => "Open URL",
            ActionType.TypeText => "Type text",
            ActionType.InsertText => "Paste plain text",
            _ => Action.ToString()
        };

        [JsonIgnore]
        public IBrush RegistrationBrush
        {
            get
            {
                if (RegistrationStatus.StartsWith("Active", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppSuccessSoftBrush");
                if (RegistrationStatus.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppDisabledForegroundBrush");
                return AppBrush("AppDangerBrush");
            }
        }

        [JsonIgnore]
        public IBrush RowBackground => IsEditing ? AppBrush("AppSelectedBrush") : TransparentBrush;

        [JsonIgnore]
        public string RegistrationStatus
        {
            get => _registrationStatus;
            set
            {
                if (_registrationStatus == value) return;
                _registrationStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RegistrationBrush));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static readonly IBrush TransparentBrush = new SolidColorBrush(Color.FromUInt32(0));

        private static IBrush AppBrush(string resourceKey)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var value) && value is IBrush brush) return brush;
                if (app != null && app.TryGetResource("AppTextPrimaryBrush", app.ActualThemeVariant, out var fallbackValue) && fallbackValue is IBrush fallback) return fallback;
            }
            catch
            {
            }

            return TransparentBrush;
        }
    }

    public class AppConfig
    {
        public List<HotkeyEntry> Hotkeys { get; set; } = new();
        public double MainWindowX { get; set; } = double.NaN;
        public double MainWindowY { get; set; } = double.NaN;
        public double MainWindowWidth { get; set; } = 700;
        public double MainWindowHeight { get; set; } = 500;
        public string MainWindowState { get; set; } = "Normal";
        public double ShortcutKeyColumnWidth { get; set; } = 220;
        public double ShortcutActionColumnWidth { get; set; } = 128;
        public double ShortcutStatusColumnWidth { get; set; } = 128;
        public double ShortcutTargetColumnWidth { get; set; } = 468;

        public double SetupWindowX { get; set; } = double.NaN;
        public double SetupWindowY { get; set; } = double.NaN;
        public double SetupWindowWidth { get; set; } = 550;
        public double SetupWindowHeight { get; set; } = 400;

        public bool UseGoogleChromeForUrls { get; set; } = true;
    }

    [JsonSerializable(typeof(AppConfig))]
    public partial class AppConfigJsonContext : JsonSerializerContext { }
}
