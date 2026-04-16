using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Controllarr.Core.Persistence
{
    // ────────────────────────────────────────────────────────────────
    // Enums
    // ────────────────────────────────────────────────────────────────

    [JsonConverter(typeof(SeedLimitActionConverter))]
    public enum SeedLimitAction
    {
        Pause,
        RemoveKeepFiles,
        RemoveDeleteFiles
    }

    [JsonConverter(typeof(RecoveryTriggerConverter))]
    public enum RecoveryTrigger
    {
        MetadataTimeout,
        NoPeers,
        StalledWithPeers,
        AwaitingRecheck,
        PostProcessMoveFailed,
        PostProcessExtractionFailed,
        DiskPressure
    }

    [JsonConverter(typeof(RecoveryActionConverter))]
    public enum RecoveryAction
    {
        Reannounce,
        Pause,
        RemoveKeepFiles,
        RemoveDeleteFiles,
        RetryPostProcess
    }

    [JsonConverter(typeof(ArrKindConverter))]
    public enum ArrKind
    {
        Sonarr,
        Radarr
    }

    // ────────────────────────────────────────────────────────────────
    // Enum JSON Converters (snake_case string serialization)
    // ────────────────────────────────────────────────────────────────

    public sealed class SeedLimitActionConverter : JsonConverter<SeedLimitAction>
    {
        public override SeedLimitAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "pause" => SeedLimitAction.Pause,
                "remove_keep_files" => SeedLimitAction.RemoveKeepFiles,
                "remove_delete_files" => SeedLimitAction.RemoveDeleteFiles,
                _ => throw new JsonException($"Unknown SeedLimitAction value: {value}")
            };
        }

        public override void Write(Utf8JsonWriter writer, SeedLimitAction value, JsonSerializerOptions options)
        {
            var str = value switch
            {
                SeedLimitAction.Pause => "pause",
                SeedLimitAction.RemoveKeepFiles => "remove_keep_files",
                SeedLimitAction.RemoveDeleteFiles => "remove_delete_files",
                _ => throw new JsonException($"Unknown SeedLimitAction value: {value}")
            };
            writer.WriteStringValue(str);
        }
    }

    public sealed class RecoveryTriggerConverter : JsonConverter<RecoveryTrigger>
    {
        public override RecoveryTrigger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "metadata_timeout" => RecoveryTrigger.MetadataTimeout,
                "no_peers" => RecoveryTrigger.NoPeers,
                "stalled_with_peers" => RecoveryTrigger.StalledWithPeers,
                "awaiting_recheck" => RecoveryTrigger.AwaitingRecheck,
                "post_process_move_failed" => RecoveryTrigger.PostProcessMoveFailed,
                "post_process_extraction_failed" => RecoveryTrigger.PostProcessExtractionFailed,
                "disk_pressure" => RecoveryTrigger.DiskPressure,
                _ => throw new JsonException($"Unknown RecoveryTrigger value: {value}")
            };
        }

        public override void Write(Utf8JsonWriter writer, RecoveryTrigger value, JsonSerializerOptions options)
        {
            var str = value switch
            {
                RecoveryTrigger.MetadataTimeout => "metadata_timeout",
                RecoveryTrigger.NoPeers => "no_peers",
                RecoveryTrigger.StalledWithPeers => "stalled_with_peers",
                RecoveryTrigger.AwaitingRecheck => "awaiting_recheck",
                RecoveryTrigger.PostProcessMoveFailed => "post_process_move_failed",
                RecoveryTrigger.PostProcessExtractionFailed => "post_process_extraction_failed",
                RecoveryTrigger.DiskPressure => "disk_pressure",
                _ => throw new JsonException($"Unknown RecoveryTrigger value: {value}")
            };
            writer.WriteStringValue(str);
        }
    }

    public sealed class RecoveryActionConverter : JsonConverter<RecoveryAction>
    {
        public override RecoveryAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "reannounce" => RecoveryAction.Reannounce,
                "pause" => RecoveryAction.Pause,
                "remove_keep_files" => RecoveryAction.RemoveKeepFiles,
                "remove_delete_files" => RecoveryAction.RemoveDeleteFiles,
                "retry_post_process" => RecoveryAction.RetryPostProcess,
                _ => throw new JsonException($"Unknown RecoveryAction value: {value}")
            };
        }

        public override void Write(Utf8JsonWriter writer, RecoveryAction value, JsonSerializerOptions options)
        {
            var str = value switch
            {
                RecoveryAction.Reannounce => "reannounce",
                RecoveryAction.Pause => "pause",
                RecoveryAction.RemoveKeepFiles => "remove_keep_files",
                RecoveryAction.RemoveDeleteFiles => "remove_delete_files",
                RecoveryAction.RetryPostProcess => "retry_post_process",
                _ => throw new JsonException($"Unknown RecoveryAction value: {value}")
            };
            writer.WriteStringValue(str);
        }
    }

    public sealed class ArrKindConverter : JsonConverter<ArrKind>
    {
        public override ArrKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "sonarr" => ArrKind.Sonarr,
                "radarr" => ArrKind.Radarr,
                _ => throw new JsonException($"Unknown ArrKind value: {value}")
            };
        }

        public override void Write(Utf8JsonWriter writer, ArrKind value, JsonSerializerOptions options)
        {
            var str = value switch
            {
                ArrKind.Sonarr => "sonarr",
                ArrKind.Radarr => "radarr",
                _ => throw new JsonException($"Unknown ArrKind value: {value}")
            };
            writer.WriteStringValue(str);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Data Classes
    // ────────────────────────────────────────────────────────────────

    public class RecoveryRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("trigger")]
        public RecoveryTrigger Trigger { get; set; } = RecoveryTrigger.MetadataTimeout;

        [JsonPropertyName("action")]
        public RecoveryAction Action { get; set; } = RecoveryAction.Reannounce;

        [JsonPropertyName("delay_minutes")]
        public int DelayMinutes { get; set; } = 10;

        public RecoveryRule() { }
    }

    public class Category
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonIgnore]
        public string Id => Name;

        [JsonPropertyName("save_path")]
        public string SavePath { get; set; } = string.Empty;

        [JsonPropertyName("complete_path")]
        public string? CompletePath { get; set; }

        [JsonPropertyName("extract_archives")]
        public bool ExtractArchives { get; set; } = false;

        [JsonPropertyName("blocked_extensions")]
        public List<string> BlockedExtensions { get; set; } = new();

        [JsonPropertyName("max_ratio")]
        public double? MaxRatio { get; set; }

        [JsonPropertyName("max_seeding_time_minutes")]
        public int? MaxSeedingTimeMinutes { get; set; }

        public Category() { }
    }

    public class BandwidthRule
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("days_of_week")]
        public List<int> DaysOfWeek { get; set; } = new();

        [JsonPropertyName("start_hour")]
        public int StartHour { get; set; } = 0;

        [JsonPropertyName("start_minute")]
        public int StartMinute { get; set; } = 0;

        [JsonPropertyName("end_hour")]
        public int EndHour { get; set; } = 23;

        [JsonPropertyName("end_minute")]
        public int EndMinute { get; set; } = 59;

        [JsonPropertyName("max_download_kbps")]
        public int? MaxDownloadKBps { get; set; }

        [JsonPropertyName("max_upload_kbps")]
        public int? MaxUploadKBps { get; set; }

        public BandwidthRule() { }
    }

    public class ArrEndpoint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonIgnore]
        public string Id => Name;

        [JsonPropertyName("kind")]
        public ArrKind Kind { get; set; } = ArrKind.Sonarr;

        [JsonPropertyName("base_url")]
        public string BaseURL { get; set; } = string.Empty;

        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        public ArrEndpoint() { }
    }

    public class Settings
    {
        [JsonPropertyName("listen_port_range_start")]
        public ushort ListenPortRangeStart { get; set; } = 49152;

        [JsonPropertyName("listen_port_range_end")]
        public ushort ListenPortRangeEnd { get; set; } = 65000;

        [JsonPropertyName("stall_threshold_minutes")]
        public int StallThresholdMinutes { get; set; } = 10;

        [JsonPropertyName("default_save_path")]
        public string DefaultSavePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Controllarr");

        [JsonPropertyName("web_ui_host")]
        public string WebUIHost { get; set; } = "127.0.0.1";

        [JsonPropertyName("web_ui_port")]
        public int WebUIPort { get; set; } = 8791;

        [JsonPropertyName("web_ui_username")]
        public string WebUIUsername { get; set; } = "admin";

        [JsonPropertyName("web_ui_password")]
        public string WebUIPassword { get; set; } = "adminadmin";

        [JsonPropertyName("global_max_ratio")]
        public double? GlobalMaxRatio { get; set; } = null;

        [JsonPropertyName("global_max_seeding_time_minutes")]
        public int? GlobalMaxSeedingTimeMinutes { get; set; } = null;

        [JsonPropertyName("seed_limit_action")]
        public SeedLimitAction SeedLimitAction { get; set; } = SeedLimitAction.Pause;

        [JsonPropertyName("minimum_seed_time_minutes")]
        public int MinimumSeedTimeMinutes { get; set; } = 60;

        [JsonPropertyName("health_stall_minutes")]
        public int HealthStallMinutes { get; set; } = 30;

        [JsonPropertyName("health_reannounce_on_stall")]
        public bool HealthReannounceOnStall { get; set; } = true;

        [JsonPropertyName("recovery_rules")]
        public List<RecoveryRule> RecoveryRules { get; set; } = new();

        [JsonPropertyName("bandwidth_schedule")]
        public List<BandwidthRule> BandwidthSchedule { get; set; } = new();

        [JsonPropertyName("vpn_enabled")]
        public bool VpnEnabled { get; set; } = false;

        [JsonPropertyName("vpn_kill_switch")]
        public bool VpnKillSwitch { get; set; } = true;

        [JsonPropertyName("vpn_bind_interface")]
        public bool VpnBindInterface { get; set; } = true;

        [JsonPropertyName("vpn_interface_prefix")]
        public string VpnInterfacePrefix { get; set; } = "TAP";

        [JsonPropertyName("vpn_monitor_interval_seconds")]
        public int VpnMonitorIntervalSeconds { get; set; } = 5;

        [JsonPropertyName("disk_space_minimum_gb")]
        public int? DiskSpaceMinimumGB { get; set; } = null;

        [JsonPropertyName("disk_space_monitor_path")]
        public string DiskSpaceMonitorPath { get; set; } = "";

        [JsonPropertyName("arr_endpoints")]
        public List<ArrEndpoint> ArrEndpoints { get; set; } = new();

        [JsonPropertyName("arr_re_search_after_hours")]
        public int ArrReSearchAfterHours { get; set; } = 6;

        public Settings() { }
    }

    public class PersistedState
    {
        [JsonPropertyName("settings")]
        public Settings Settings { get; set; } = new();

        [JsonPropertyName("categories")]
        public List<Category> Categories { get; set; } = new();

        [JsonPropertyName("category_by_hash")]
        public Dictionary<string, string> CategoryByHash { get; set; } = new();

        [JsonPropertyName("last_known_good_port")]
        public ushort? LastKnownGoodPort { get; set; } = null;

        public PersistedState() { }
    }
}
