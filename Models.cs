using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public class HotkeyEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string KeyCombination { get; set; } = string.Empty; // e.g. "Ctrl+Alt+A"
        public ActionType Action { get; set; }
        public string Target { get; set; } = string.Empty;
    }

    public class AppConfig
    {
        public List<HotkeyEntry> Hotkeys { get; set; } = new();
        public double MainWindowX { get; set; } = double.NaN;
        public double MainWindowY { get; set; } = double.NaN;
        public double MainWindowWidth { get; set; } = 700;
        public double MainWindowHeight { get; set; } = 500;
        public string MainWindowState { get; set; } = "Normal";

        public double SetupWindowX { get; set; } = double.NaN;
        public double SetupWindowY { get; set; } = double.NaN;
        public double SetupWindowWidth { get; set; } = 550;
        public double SetupWindowHeight { get; set; } = 400;
    }

    [JsonSerializable(typeof(AppConfig))]
    public partial class AppConfigJsonContext : JsonSerializerContext { }
}
