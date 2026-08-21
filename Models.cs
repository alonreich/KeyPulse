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

        [JsonPropertyName("Target")]
        public string EncryptedTarget
        {
            get => CryptoHelper.Protect(_target);
            set
            {
                if (CryptoHelper.TryUnprotect(value, out var plain))
                {
                    TargetUnreadable = false;
                    Target = plain;
                }
                else
                {
                    TargetUnreadable = true;
                    Target = string.Empty;
                }
            }
        }

        // ------------------------------------------------------------------
        // ISSUE_5: live registration bookkeeping so one shortcut can be changed
        // without tearing down and re-registering every other shortcut.
        // ------------------------------------------------------------------

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
        public string TargetTooltip => IsTargetHidden
            ? "Hidden. Select this row to view or change the value."
            : Target;

        /// <summary>ISSUE_2: strong enough that no glyph shape survives it.</summary>
        [JsonIgnore]
        public double TargetBlurRadius => IsTargetHidden ? 12.0 : 0.0;

        [JsonIgnore]
        public IBrush TargetDisplayBrush => IsTargetObfuscated ? AppBrush("AppDisabledForegroundBrush") : AppBrush("AppTextPrimaryBrush");

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
                if (!IsEnabled) return AppBrush("AppDangerBrush"); // Dark red if disabled
                return AppBrush("AppPanelRaisedBrush"); // Normal button color
            }
        }

        [JsonIgnore]
        public IBrush StatusButtonForeground
        {
            get
            {
                if (!IsEnabled) return AppBrush("AppOnAccentTextBrush"); // White text on dark red
                return RegistrationBrush; // Colored text for active/waiting/error
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
            }
        }

        [JsonIgnore]
        public string StatusTooltip => string.IsNullOrWhiteSpace(StatusHint)
            ? RegistrationStatus
            : RegistrationStatus + "\n" + StatusHint;

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

        /// <summary>Mirror of the Windows startup state so elevating/de-elevating cannot silently lose it.</summary>
        public bool LaunchOnBoot { get; set; }

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

    // ----------------------------------------------------------------------
    // ISSUE_1 / ISSUE_24: a backup is its own file format, not a raw copy of config.json.
    //
    // Three things forced this.
    //   1. "Restore" used to accept ANY json file: a file that simply did not mention shortcuts
    //      deserialized into an empty list, passed validation, and wiped every shortcut the user
    //      had. A backup must identify itself before it is allowed to replace anything.
    //   2. Targets on disk are tied to the Windows account (ISSUE_3), so a straight file copy would
    //      restore into unreadable junk on a new PC - the one case backups exist for.
    //   3. A plain-text export of targets is a plain-text export of whatever the user hid behind
    //      the Blur/Obfuscate checkbox. The payload is therefore optionally encrypted with a
    //      passphrase the user chooses, and is integrity-checked either way.
    //
    // The envelope is always readable so the file can identify itself, be dated, and be recognised
    // as encrypted BEFORE anyone is asked for a passphrase. Only the payload is protected.
    // ----------------------------------------------------------------------

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

        // NOTE: AppConfig.LaunchOnBoot is deliberately NOT carried. Whether KeyPulse starts with
        // Windows is a property of the machine, held in the registry or a scheduled task. Silently
        // switching that on because a backup from another PC had it on would be an unpleasant
        // surprise, so restore leaves it exactly as it is and says so.
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
