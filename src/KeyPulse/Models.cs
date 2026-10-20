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
        InsertText,
        CycleBrightness
    }

    public class HotkeyEntry : INotifyPropertyChanged
    {
        private string _registrationStatus = "Pending";
        private string _statusHint = string.Empty;
        private bool _isEnabled = true;
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

        private string? _displayName;
        public string? DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActionTitle));
            }
        }

        private List<string> _keyCombinations = new();
        [JsonPropertyName("KeyCombinations")]
        public List<string> KeyCombinations
        {
            get => _keyCombinations;
            set
            {
                _keyCombinations = value ?? new List<string>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyCombination));
                OnPropertyChanged(nameof(KeyCombinationsDisplay));
                OnPropertyChanged(nameof(KeyCombinationsTooltip));
            }
        }

        [JsonPropertyName("KeyCombination")]
        public string? LegacyKeyCombination
        {
            get => _keyCombinations.Count > 0 ? _keyCombinations[0] : null;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && _keyCombinations.Count == 0)
                {
                    _keyCombinations.Add(value.Trim());
                    OnPropertyChanged(nameof(KeyCombinations));
                    OnPropertyChanged(nameof(KeyCombination));
                    OnPropertyChanged(nameof(KeyCombinationsDisplay));
                    OnPropertyChanged(nameof(KeyCombinationsTooltip));
                }
            }
        }

        [JsonIgnore]
        public string KeyCombination
        {
            get => _keyCombinations.Count > 0 ? _keyCombinations[0] : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (_keyCombinations.Count > 0)
                    {
                        _keyCombinations.RemoveAt(0);
                    }
                }
                else
                {
                    if (_keyCombinations.Count == 0)
                    {
                        _keyCombinations.Add(value.Trim());
                    }
                    else
                    {
                        _keyCombinations[0] = value.Trim();
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyCombinations));
                OnPropertyChanged(nameof(KeyCombinationsDisplay));
                OnPropertyChanged(nameof(KeyCombinationsTooltip));
            }
        }

        [JsonIgnore]
        public string KeyCombinationsDisplay
        {
            get
            {
                if (_keyCombinations == null || _keyCombinations.Count == 0) return "No hotkey";
                if (_keyCombinations.Count == 1) return _keyCombinations[0];
                return $"{_keyCombinations[0]} (+{_keyCombinations.Count - 1} more)";
            }
        }

        [JsonIgnore]
        public string KeyCombinationsTooltip
        {
            get
            {
                if (_keyCombinations == null || _keyCombinations.Count == 0)
                    return "No keyboard shortcut assigned. Run manually or from Quick Actions.";
                if (_keyCombinations.Count == 1)
                    return _keyCombinations[0];
                return string.Join("\n", _keyCombinations);
            }
        }

        [JsonIgnore]
        public string ActionTitle => !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName
            : ActionDisplay;

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
                if (TargetUnreadable && UnreadableTargetCipher.Length > 0) return UnreadableTargetCipher;
                if (TargetProtectionFailed && _target.Length > 0) return _target;
                return CryptoHelper.Protect(_target);
            }
            set
            {
                if (CryptoHelper.TryUnprotect(value, out var plain))
                {
                    TargetUnreadable = false;
                    UnreadableTargetCipher = string.Empty;
                    TargetProtectionFailed = CryptoHelper.ProtectionUnavailable && IsTargetObfuscated;
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

        /// <summary>Mapping of Normalized Combination -> Win32 Hotkey ID for multiple bindings.</summary>
        [JsonIgnore]
        public Dictionary<string, int> RegisteredHotkeyIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-combination status (Active, Conflict, etc.).</summary>
        [JsonIgnore]
        public Dictionary<string, string> ComboStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-combination hints or suggestions.</summary>
        [JsonIgnore]
        public Dictionary<string, string> ComboHints { get; } = new(StringComparer.OrdinalIgnoreCase);

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
            ActionType.CycleBrightness => "Cycle brightness",
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

        private bool _addToQuickActionMenu;
        public bool AddToQuickActionMenu
        {
            get => _addToQuickActionMenu;
            set
            {
                if (_addToQuickActionMenu == value) return;
                _addToQuickActionMenu = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QuickActionIndicator));
                OnPropertyChanged(nameof(QuickActionTooltip));
            }
        }

        [JsonIgnore]
        public string QuickActionIndicator => AddToQuickActionMenu ? "⚡" : string.Empty;

        [JsonIgnore]
        public string QuickActionTooltip => AddToQuickActionMenu ? "In Quick Action Menu (Right-click system tray)" : string.Empty;

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
                return TargetProtectionFailed
                    ? "Hidden from the list, but stored UNENCRYPTED in the settings file because Windows could not protect it. Anyone who opens that file can read it."
                    : "Hidden. Select this row to view or change the value.";
            }
        }

        /// <summary>ISSUE_2: strong enough that no glyph shape survives it.</summary>
        [JsonIgnore]
        public double TargetBlurRadius => IsTargetHidden ? 12.0 : 0.0;

        [JsonIgnore]
        public string TargetUnencryptedBadge => TargetProtectionFailed && IsTargetObfuscated
            ? " ⚠️ Unencrypted"
            : string.Empty;

        [JsonIgnore]
        public IBrush TargetDisplayBrush => TargetProtectionFailed && IsTargetObfuscated
            ? AppBrush("AppWarningBrush")
            : (IsTargetObfuscated ? AppBrush("AppDisabledForegroundBrush") : AppBrush("AppTextPrimaryBrush"));

        [JsonIgnore]
        public IBrush RegistrationBrush
        {
            get
            {
                if (RegistrationStatus.StartsWith("Active", StringComparison.OrdinalIgnoreCase)
                    || RegistrationStatus.StartsWith("Ready", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppSuccessSoftBrush");
                if (RegistrationStatus.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppDisabledForegroundBrush");
                if (RegistrationStatus.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase)
                    || RegistrationStatus.StartsWith("Partial", StringComparison.OrdinalIgnoreCase)) return AppBrush("AppWarningBrush");
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
        public int SchemaVersion { get; set; } = 1;
        public bool AutoCheckForUpdates { get; set; } = true;
        public string UpdateChannel { get; set; } = "Stable";
        public string SkippedUpdateVersion { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

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
        public double SettingsWindowWidth { get; set; } = 640;
        public double SettingsWindowHeight { get; set; } = 680;
        public string SettingsWindowState { get; set; } = "Normal";
        public int LastActiveSettingsTab { get; set; } = 0;

        public double DesktopOrganizerWindowX { get; set; } = double.NaN;
        public double DesktopOrganizerWindowY { get; set; } = double.NaN;
        public double DesktopOrganizerWindowWidth { get; set; } = 820;
        public double DesktopOrganizerWindowHeight { get; set; } = 640;

        public double ScreenBrightnessWindowX { get; set; } = double.NaN;
        public double ScreenBrightnessWindowY { get; set; } = double.NaN;
        public double ScreenBrightnessWindowWidth { get; set; } = 880;
        public double ScreenBrightnessWindowHeight { get; set; } = 740;

        public bool UseGoogleChromeForUrls { get; set; } = false;
        public string PreferredBrowser { get; set; } = "System";

        /// <summary>
        /// Mirror of the Windows startup state so elevating/de-elevating cannot silently lose it.
        /// ISSUE_33: defaults to true - a fresh install starts with Windows unless the user
        /// turns it off; MainWindow turns that default into a real launcher on the first run.
        /// </summary>
        public bool LaunchOnBoot { get; set; } = true;

        public bool SoundEnabled { get; set; } = true;

        /// <summary>
        /// ISSUE_9: milliseconds paused between batches of injected characters. 1 = fast (default),
        /// 6 = normal, 18 = compatible, or custom 1..200 ms for old consoles and remote sessions.
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
        public string? DisplayName { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool AllowRiskyShortcut { get; set; }

        [JsonPropertyName("KeyCombinations")]
        public List<string> KeyCombinations { get; set; } = new();

        [JsonPropertyName("KeyCombination")]
        public string? LegacyKeyCombination
        {
            get => KeyCombinations.Count > 0 ? KeyCombinations[0] : null;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && KeyCombinations.Count == 0)
                {
                    KeyCombinations.Add(value.Trim());
                }
            }
        }

        [JsonIgnore]
        public string KeyCombination
        {
            get => KeyCombinations.Count > 0 ? KeyCombinations[0] : string.Empty;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (KeyCombinations.Count > 0) KeyCombinations.RemoveAt(0);
                }
                else
                {
                    if (KeyCombinations.Count == 0) KeyCombinations.Add(value.Trim());
                    else KeyCombinations[0] = value.Trim();
                }
            }
        }

        public ActionType Action { get; set; }
        public string Target { get; set; } = string.Empty;
        public bool IsTargetObfuscated { get; set; }
        public bool AddToQuickActionMenu { get; set; }
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
        public string SettingsWindowState { get; set; } = "Normal";

        public double SetupWindowX { get; set; } = double.NaN;
        public double SetupWindowY { get; set; } = double.NaN;
        public double SetupWindowWidth { get; set; }
        public double SetupWindowHeight { get; set; }

        public double DesktopOrganizerWindowX { get; set; } = double.NaN;
        public double DesktopOrganizerWindowY { get; set; } = double.NaN;
        public double DesktopOrganizerWindowWidth { get; set; }
        public double DesktopOrganizerWindowHeight { get; set; }

        public double ScreenBrightnessWindowX { get; set; } = double.NaN;
        public double ScreenBrightnessWindowY { get; set; } = double.NaN;
        public double ScreenBrightnessWindowWidth { get; set; }
        public double ScreenBrightnessWindowHeight { get; set; }

        public double ShortcutStatusColumnWidth { get; set; }
        public double ShortcutKeyColumnWidth { get; set; }
        public double ShortcutActionColumnWidth { get; set; }
        public double ShortcutTargetColumnWidth { get; set; }
    }

    /// <summary>Everything a restore puts back. Serialized, hashed, then optionally encrypted.</summary>
    public class BackupPayload
    {
        public List<BackupShortcut> Shortcuts { get; set; } = new();

        public bool UseGoogleChromeForUrls { get; set; } = false;
        public string PreferredBrowser { get; set; } = "System";
        public bool SoundEnabled { get; set; } = true;
        public int TypingDelayMs { get; set; } = 1;
        public string Theme { get; set; } = "System";
        public string ShortcutSortColumn { get; set; } = string.Empty;
        public bool ShortcutSortDescending { get; set; }
        public bool HasSeenTrayHint { get; set; }
        public int LastActiveSettingsTab { get; set; }
        public bool AutoCheckForUpdates { get; set; } = true;
        public string UpdateChannel { get; set; } = "Stable";
        public string SkippedUpdateVersion { get; set; } = string.Empty;

        public BackupWindowLayout Window { get; set; } = new();

        public DesktopOrganizerConfig? DesktopOrganizer { get; set; }

        public ScreenBrightnessConfig? ScreenBrightness { get; set; }
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

    public enum UnsavedIconBehavior
    {
        CenterGrid,
        LeaveInPlace,
        SideColumn,
        SecondaryMonitorCenterGrid
    }

    public class DesktopIconSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public int MonitorIndex { get; set; } = 1;
        public int RelativeX { get; set; }
        public int RelativeY { get; set; }
        public int AbsoluteX { get; set; }
        public int AbsoluteY { get; set; }
    }

    public class DesktopMonitorSnapshot
    {
        public int Index { get; set; } = 1;
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }
    }

    public class DesktopLayoutProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Default Layout";
        public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public int DisplayCount { get; set; }
        public int IconSpacingX { get; set; } = 76;
        public int IconSpacingY { get; set; } = 100;
        public bool IsAutoSnapshot { get; set; } = false;
        public List<DesktopMonitorSnapshot> Monitors { get; set; } = new();
        public List<DesktopIconSnapshot> Icons { get; set; } = new();

        [JsonIgnore]
        public int IconCount => Icons?.Count ?? 0;
    }

    public class DesktopOrganizerConfig
    {
        public int SchemaVersion { get; set; } = 1;
        public bool AutoRestoreOnDisplayChange { get; set; } = false;
        public bool AutoPeriodicSnapshotEnabled { get; set; } = false;
        public int AutoSnapshotIntervalDays { get; set; } = 3;
        public string LastAutoSnapshotUtc { get; set; } = string.Empty;
        public UnsavedIconBehavior NewIconBehavior { get; set; } = UnsavedIconBehavior.CenterGrid;
        public string ActiveProfileId { get; set; } = string.Empty;
        public List<DesktopLayoutProfile> Profiles { get; set; } = new();
    }

    public class ScreenHardwareSetting
    {
        public int SlotIndex { get; set; }
        public string HardwareId { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string CustomNickname { get; set; } = string.Empty;
        public bool LockRatio { get; set; } = true;
        public int LastBrightness { get; set; } = 50;
        public int LastContrast { get; set; } = 50;
        public int VirtualBrightness { get; set; } = 50;
        public int VirtualContrast { get; set; } = 50;
        public bool IsLinkedToNext { get; set; } = false;
    }

    public class ScreenCycleEntry
    {
        public int SlotIndex { get; set; }
        public string HardwareId { get; set; } = string.Empty;
        public int Brightness { get; set; } = 50;
        public int Contrast { get; set; } = 50;
        public bool Enabled { get; set; } = true;
    }

    public class ScreenBrightnessProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Preset";
        public bool ShowInTrayQuickActions { get; set; } = true;
        public bool IncludeInCycle { get; set; } = true;
        public string Hotkey { get; set; } = string.Empty;
        public List<ScreenCycleEntry> Targets { get; set; } = new();
    }

    public class ScreenBrightnessConfig
    {
        public int SchemaVersion { get; set; } = 1;
        public bool GlobalLockBrightnessContrast { get; set; } = true;
        public string LastCycleProfileId { get; set; } = string.Empty;
        public string CycleUpHotkey { get; set; } = string.Empty;
        public string CycleDownHotkey { get; set; } = string.Empty;
        public string StepUpHotkey { get; set; } = string.Empty;
        public string StepDownHotkey { get; set; } = string.Empty;
        public int StepDelta { get; set; } = 5;
        public bool LockMonitorDifferential { get; set; } = true;
        public double OverlayWindowX { get; set; } = double.NaN;
        public double OverlayWindowY { get; set; } = double.NaN;
        public double OverlayWindowWidth { get; set; } = 700;
        public double OverlayWindowHeight { get; set; } = 540;
        public List<ScreenHardwareSetting> Screens { get; set; } = new();
        public List<ScreenBrightnessProfile> Profiles { get; set; } = new();
        public List<ScreenDirectHotkey> DirectHotkeys { get; set; } = new();
    }

    public class ScreenDirectHotkey
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Hotkey { get; set; } = string.Empty;
        /// <summary>
        /// 0 = Screen A, 1 = Screen B, 2 = Screen C, -1 = All Screens.
        /// </summary>
        public int TargetSlotIndex { get; set; } = 0;
        public int Brightness { get; set; } = 65;
        public int Contrast { get; set; } = 65;
        public bool LockRatio { get; set; } = true;
    }

    [JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(BackupEnvelope))]
    [JsonSerializable(typeof(BackupPayload))]
    [JsonSerializable(typeof(GitHubRelease))]
    [JsonSerializable(typeof(List<GitHubRelease>))]
    [JsonSerializable(typeof(GitHubReleaseAsset))]
    [JsonSerializable(typeof(List<GitHubReleaseAsset>))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(DesktopOrganizerConfig))]
    [JsonSerializable(typeof(DesktopLayoutProfile))]
    [JsonSerializable(typeof(DesktopIconSnapshot))]
    [JsonSerializable(typeof(DesktopMonitorSnapshot))]
    [JsonSerializable(typeof(List<DesktopLayoutProfile>))]
    [JsonSerializable(typeof(List<DesktopIconSnapshot>))]
    [JsonSerializable(typeof(List<DesktopMonitorSnapshot>))]
    [JsonSerializable(typeof(ScreenBrightnessConfig))]
    [JsonSerializable(typeof(ScreenBrightnessProfile))]
    [JsonSerializable(typeof(ScreenCycleEntry))]
    [JsonSerializable(typeof(ScreenHardwareSetting))]
    [JsonSerializable(typeof(ScreenDirectHotkey))]
    [JsonSerializable(typeof(List<ScreenBrightnessProfile>))]
    [JsonSerializable(typeof(List<ScreenCycleEntry>))]
    [JsonSerializable(typeof(List<ScreenHardwareSetting>))]
    [JsonSerializable(typeof(List<ScreenDirectHotkey>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(List<HotkeyEntry>))]
    [JsonSerializable(typeof(List<BackupShortcut>))]
    [JsonSerializable(typeof(IpcMessage))]
    [JsonSerializable(typeof(IpcResponse))]
    public partial class AppConfigJsonContext : JsonSerializerContext { }
}
