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
        private string _statusHint = string.Empty;
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
                OnPropertyChanged(nameof(StatusButtonBackground));
                OnPropertyChanged(nameof(StatusButtonForeground));
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(ToggleTooltip));
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
                OnPropertyChanged(nameof(TargetDisplay));
                OnPropertyChanged(nameof(TargetTooltip));
                OnPropertyChanged(nameof(TargetBlurRadius));
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
        }

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

        [JsonIgnore]
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
                OnPropertyChanged(nameof(TargetDisplay));
                OnPropertyChanged(nameof(TargetTooltip));
                OnPropertyChanged(nameof(EncryptedTarget));
            }
        }

        /// <summary>
        /// ISSUE_3: set when the stored target could not be decrypted on this Windows account.
        /// The row is then reported as broken instead of pretending the cipher text is a real path.
        /// </summary>
        [JsonIgnore]
        public bool TargetUnreadable { get; set; }

        /// <summary>
        /// ISSUE_1: the exact stored value from the settings file when it could not be decrypted on
        /// this Windows account. It is written back byte-for-byte until the user types a replacement,
        /// so a config that is simply moved back to the right account recovers every shortcut.
        /// </summary>
        [JsonIgnore]
        public string UnreadableTargetCipher { get; set; } = string.Empty;

        /// <summary>
        /// ISSUE_10: this target is sitting in the settings file as readable text because Windows
        /// could not protect it. The list stops implying a protection that is not there.
        /// </summary>
        [JsonIgnore]
        public bool TargetProtectionFailed { get; set; }

        /// <summary>
        /// ISSUE_4: last background reachability answer for this row. Null while a check is running,
        /// so the UI never has to touch a disk or the network to paint a status.
        /// </summary>
        [JsonIgnore]
        public bool? TargetReachable { get; set; }

        [JsonPropertyName("Target")]
        public string EncryptedTarget
        {
            get
            {
                // ISSUE_1: while the target cannot be decrypted, hand back the ORIGINAL cipher text.
                // Re-protecting the now-empty _target here used to overwrite the stored value with an
                // empty string the first time anything saved, destroying the shortcut forever right
                // after the app had promised "Nothing was deleted".
                if (TargetUnreadable && UnreadableTargetCipher.Length > 0) return UnreadableTargetCipher;
                // ISSUE_10: never re-protect a value that could not be protected - it would fail again
                // anyway, and quietly re-writing plain text hides the problem from the user.
                if (TargetProtectionFailed && _target.Length > 0) return _target;
                return CryptoHelper.Protect(_target);
            }
            set
            {
                if (CryptoHelper.TryUnprotect(value, out var plain))
                {
                    TargetUnreadable = false;
                    UnreadableTargetCipher = string.Empty;
                    // ISSUE_10: a value stored without the DPAPI/legacy prefix was written in plain
                    // text because Windows could not protect it. Flag it so the row is honest.
                    TargetProtectionFailed = value.Length > 0 && !CryptoHelper.IsProtectedValue(value);
                    Target = plain;
                }
                else
                {
                    TargetUnreadable = true;
                    UnreadableTargetCipher = value ?? string.Empty;
                    Target = string.Empty;
                }
            }
        }


        /// <summary>Win32 hotkey id currently held by this row, or 0 when it holds none.</summary>
        [JsonIgnore]
        public int RegisteredHotkeyId { get; set; }

        /// <summary>The normalized combination RegisteredHotkeyId was taken out for.</summary>
        [JsonIgnore]
        public string RegisteredCombo { get; set; } = string.Empty;

        /// <summary>The combination the current conflict suggestion was computed for.</summary>
        [JsonIgnore]
        public string SuggestedForCombo { get; set; } = string.Empty;

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
            ActionType.BrowseChrome => "Open web link",
            ActionType.TypeText => "Type text",
            ActionType.InsertText => "Paste text",
            _ => Action.ToString()
        };

        private bool _isTargetObfuscated;
        public bool IsTargetObfuscated
        {
            get => _isTargetObfuscated;
            set
            {
                if (_isTargetObfuscated == value) return;
                _isTargetObfuscated = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TargetDisplayBrush));
                OnPropertyChanged(nameof(TargetBlurRadius));
                OnPropertyChanged(nameof(TargetDisplay));
                OnPropertyChanged(nameof(TargetTooltip));
            }
        }

        /// <summary>True while the value must not be shown anywhere in the list.</summary>
        [JsonIgnore]
        public bool IsTargetHidden => IsTargetObfuscated && !IsEditing;

        /// <summary>
        /// ISSUE_2: the list NEVER renders a hidden target's real characters. The old row bound the
        /// raw value and relied on a blur, so the text was recoverable by squinting, by screenshot,
        /// and outright readable in the row's tool tip. A mask is rendered instead, and the strong
        /// blur on top of it exists only so the row reads as deliberately concealed.
        /// </summary>
        [JsonIgnore]
        public string TargetDisplay
        {
            get
            {
                if (!IsTargetHidden) return Target;
                var length = string.IsNullOrEmpty(Target) ? 0 : Math.Clamp(Target.Length, 8, 32);
                return new string('●', length);
            }
        }

        [JsonIgnore]
        public string TargetTooltip
        {
            get
            {
                if (!IsTargetHidden) return Target;
                // ISSUE_10: when Windows could not encrypt the value, say so instead of implying
                // the blur means the file on disk is protected.
                return TargetProtectionFailed
                    ? "Hidden from the list, but stored UNENCRYPTED in the settings file because Windows could not protect it. Anyone who opens that file can read it."
                    : "Hidden. Select this row to view or change the value.";
            }
        }

        /// <summary>ISSUE_2: strong enough that no glyph shape survives it.</summary>
        [JsonIgnore]
        public double TargetBlurRadius => IsTargetHidden ? 12.0 : 0.0;

        [JsonIgnore]
        public IBrush TargetDisplayBrush => TargetProtectionFailed && IsTargetObfuscated
            ? AppBrush("AppWarningBrush")
            : (IsTargetObfuscated ? AppBrush("AppDisabledForegroundBrush") : AppBrush("AppTextPrimaryBrush"));

        [JsonIgnore]
        public IBrush RegistrationBrush
        {
            get
            {
                if (RegistrationStatus.StartsWith("Active", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppSuccessSoftBrush");
                if (RegistrationStatus.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppDisabledForegroundBrush");
                if (RegistrationStatus.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppWarningBrush");
                return AppBrush("AppDangerBrush");
            }
        }

        [JsonIgnore]
        public IBrush StatusButtonBackground
        {
            get
            {
                if (!IsEnabled) return AppBrush("AppDangerBrush");
                return AppBrush("AppPanelRaisedBrush");
            }
        }

        [JsonIgnore]
        public IBrush StatusButtonForeground
        {
            get
            {
                if (!IsEnabled) return AppBrush("AppOnAccentTextBrush");
                return RegistrationBrush;
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
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusButtonBackground));
                OnPropertyChanged(nameof(StatusButtonForeground));
            }
        }

        /// <summary>Human-readable follow-up for a non-Active status, e.g. a free combination to try instead.</summary>
        [JsonIgnore]
        public string StatusHint
        {
            get => _statusHint;
            set
            {
                if (_statusHint == value) return;
                _statusHint = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusTooltip));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }

        [JsonIgnore]
        public string StatusTooltip => string.IsNullOrWhiteSpace(StatusHint)
            ? RegistrationStatus
            : RegistrationStatus + "\n" + StatusHint;

        /// <summary>
        /// ISSUE_6: the full reason, in words, for the Status column. Reading a status must never
        /// change anything, so the column is a plain label now - not a disguised toggle button that
        /// clipped to "Inactive: C..." and silently switched the shortcut off when clicked.
        /// </summary>
        [JsonIgnore]
        public string StatusLabel => string.IsNullOrWhiteSpace(StatusHint)
            ? RegistrationStatus
            : RegistrationStatus + ": " + StatusHint;

        /// <summary>ISSUE_6: what the separate on/off control says.</summary>
        [JsonIgnore]
        public string ToggleLabel => IsEnabled ? "On" : "Off";

        /// <summary>ISSUE_6: what the separate on/off control explains.</summary>
        [JsonIgnore]
        public string ToggleTooltip => IsEnabled
            ? "Switch this shortcut off. The keys are released and nothing is deleted."
            : "Switch this shortcut back on.";

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
        public double MainWindowWidth { get; set; } = 1640;
        public double MainWindowHeight { get; set; } = 975;
        public string MainWindowState { get; set; } = "Normal";
        public double ShortcutKeyColumnWidth { get; set; } = 150;
        public double ShortcutActionColumnWidth { get; set; } = 200;
        public double ShortcutStatusColumnWidth { get; set; } = 128;
        public double ShortcutTargetColumnWidth { get; set; } = 468;

        public double SetupWindowX { get; set; } = double.NaN;
        public double SetupWindowY { get; set; } = double.NaN;
        public double SetupWindowWidth { get; set; } = 550;
        public double SetupWindowHeight { get; set; } = 400;

        public double SettingsWindowX { get; set; } = double.NaN;
        public double SettingsWindowY { get; set; } = double.NaN;
        public double SettingsWindowWidth { get; set; } = 580;
        public double SettingsWindowHeight { get; set; } = 750;

        public bool UseGoogleChromeForUrls { get; set; } = true;

        /// <summary>
        /// Mirror of the Windows startup state so elevating/de-elevating cannot silently lose it.
        /// ISSUE_33: defaults to true - a fresh install starts with Windows unless the user
        /// turns it off; MainWindow turns that default into a real launcher on the first run.
        /// </summary>
        public bool LaunchOnBoot { get; set; } = true;

        public bool SoundEnabled { get; set; } = true;

        /// <summary>
        /// ISSUE_9: milliseconds paused between batches of injected characters. 1 = fast (default),
        /// 6 = normal, 18 = compatible, for old consoles and remote sessions that drop fast input.
        /// </summary>
        public int TypingDelayMs { get; set; } = 1;

        /// <summary>"System", "Light" or "Dark".</summary>
        public string Theme { get; set; } = "System";

        /// <summary>The one-time "KeyPulse is still running in the tray" hint has been shown.</summary>
        public bool HasSeenTrayHint { get; set; }

        /// <summary>"", "Shortcut", "Action", "Status" or "Target".</summary>
        public string ShortcutSortColumn { get; set; } = string.Empty;

        public bool ShortcutSortDescending { get; set; }

        /// <summary>Set when the on-disk config could not be read; blocks saving so nothing is overwritten.</summary>
        [JsonIgnore]
        public bool IsReadOnlySession { get; set; }
    }


    public class BackupShortcut
    {
        public string Id { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool AllowRiskyShortcut { get; set; }
        public string KeyCombination { get; set; } = string.Empty;
        public ActionType Action { get; set; }
        public string Target { get; set; } = string.Empty;
        public bool IsTargetObfuscated { get; set; }
    }

    /// <summary>
    /// ISSUE_24: window geometry and column widths travel with the backup. Restoring them is safe
    /// only because BackupService validates every value against the monitors actually attached to
    /// the machine being restored onto - a position from a three-monitor desk would otherwise put
    /// the window somewhere the user cannot reach it.
    /// </summary>
    public class BackupWindowLayout
    {
        public double MainWindowX { get; set; } = double.NaN;
        public double MainWindowY { get; set; } = double.NaN;
        public double MainWindowWidth { get; set; }
        public double MainWindowHeight { get; set; }
        public string MainWindowState { get; set; } = "Normal";

        public double SettingsWindowX { get; set; } = double.NaN;
        public double SettingsWindowY { get; set; } = double.NaN;
        public double SettingsWindowWidth { get; set; }
        public double SettingsWindowHeight { get; set; }

        public double SetupWindowX { get; set; } = double.NaN;
        public double SetupWindowY { get; set; } = double.NaN;
        public double SetupWindowWidth { get; set; }
        public double SetupWindowHeight { get; set; }

        public double ShortcutStatusColumnWidth { get; set; }
        public double ShortcutKeyColumnWidth { get; set; }
        public double ShortcutActionColumnWidth { get; set; }
        public double ShortcutTargetColumnWidth { get; set; }
    }

    /// <summary>Everything a restore puts back. Serialized, hashed, then optionally encrypted.</summary>
    public class BackupPayload
    {
        public List<BackupShortcut> Shortcuts { get; set; } = new();

        public bool UseGoogleChromeForUrls { get; set; } = true;
        public bool SoundEnabled { get; set; } = true;
        public int TypingDelayMs { get; set; } = 1;
        public string Theme { get; set; } = "System";
        public string ShortcutSortColumn { get; set; } = string.Empty;
        public bool ShortcutSortDescending { get; set; }
        public bool HasSeenTrayHint { get; set; }

        public BackupWindowLayout Window { get; set; } = new();

    }

    public class BackupEnvelope
    {
        /// <summary>Must be exactly BackupFileType, or the file is refused.</summary>
        public string FileType { get; set; } = BackupFileType;

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string CreatedUtc { get; set; } = string.Empty;
        public string CreatedByVersion { get; set; } = string.Empty;

        /// <summary>"None" or "AesGcmPbkdf2".</summary>
        public string Encryption { get; set; } = EncryptionNone;

        public int KdfIterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string AuthTag { get; set; } = string.Empty;

        /// <summary>SHA-256 of the PLAINTEXT payload bytes, lower-case hex. Catches silent corruption.</summary>
        public string PayloadSha256 { get; set; } = string.Empty;

        /// <summary>Base64 of the payload: cipher text when encrypted, plain UTF-8 json when not.</summary>
        public string Payload { get; set; } = string.Empty;

        public const string BackupFileType = "KeyPulseBackup";
        public const int CurrentFormatVersion = 2;
        public const string EncryptionNone = "None";
        public const string EncryptionAesGcm = "AesGcmPbkdf2";
    }

    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(BackupEnvelope))]
    [JsonSerializable(typeof(BackupPayload))]
    [JsonSerializable(typeof(GitHubRelease))]
    public partial class AppConfigJsonContext : JsonSerializerContext { }
}
