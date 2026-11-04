using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Attributes;

namespace Emby.Subremover
{
    public enum SettingsTabId
    {
        Run,
        Subtitles,
        SubtitleFiles,
        Backups,
        Logs
    }

    /// <summary>
    /// What the settings page shows on each tab, in order: headings, settings and the page's own parts (the run status,
    /// the restore list, the logs). Each setting's label, description, limits and when it shows come from its property in
    /// <see cref="PluginOptions"/>, so they're written once.
    /// </summary>
    internal static class SettingsLayout
    {
        internal const string Heading = "heading";
        internal const string Field = "field";
        internal const string Widget = "widget";

        // The page's own parts, drawn by settings.js.
        internal const string RunWidget = "run";
        internal const string QuickSetupWidget = "quickSetup";
        internal const string RestoreWidget = "restore";
        internal const string LogsWidget = "logs";

        internal sealed class TabLayout
        {
            public SettingsTabId Id;
            public string Name;
            public (string Kind, string Value)[] Entries;
        }

        private static (string, string) H(string text) => (Heading, text);
        private static (string, string) F(string property) => (Field, property);
        private static (string, string) W(string widget) => (Widget, widget);

        internal static readonly TabLayout[] Tabs =
        {
            new TabLayout
            {
                Id = SettingsTabId.Run, Name = "Run", Entries = new[]
                {
                    W(RunWidget),
                    F(nameof(PluginOptions.DryRun)),
                    F(nameof(PluginOptions.Libraries)),
                    F(nameof(PluginOptions.MaxItemsPerRun)),
                    F(nameof(PluginOptions.OnlyPathContains)),
                    F(nameof(PluginOptions.ProcessNewVideos)),
                    H("Advanced"),
                    F(nameof(PluginOptions.ShowInSidebar)),
                    F(nameof(PluginOptions.ReprocessAll))
                }
            },
            new TabLayout
            {
                Id = SettingsTabId.Subtitles, Name = "Subtitles", Entries = new[]
                {
                    H("Languages"),
                    W(QuickSetupWidget),
                    F(nameof(PluginOptions.KeepLanguages)),
                    F(nameof(PluginOptions.KeepUndefined)),
                    F(nameof(PluginOptions.GuessLanguages)),
                    F(nameof(PluginOptions.GuessForFilesOnly)),
                    F(nameof(PluginOptions.KeepForcedAnyLanguage)),
                    H("Remove"),
                    F(nameof(PluginOptions.RemoveHearingImpaired)),
                    F(nameof(PluginOptions.RemoveCommentary)),
                    H("Default track"),
                    F(nameof(PluginOptions.MakeRegularDefault)),
                    F(nameof(PluginOptions.DefaultLanguage))
                }
            },
            new TabLayout
            {
                Id = SettingsTabId.SubtitleFiles, Name = "Subtitle files", Entries = new[]
                {
                    H("Extract"),
                    F(nameof(PluginOptions.ExtractTextSubtitles)),
                    F(nameof(PluginOptions.RemoveExtractedFromVideo)),
                    F(nameof(PluginOptions.MarkDefaultInName)),
                    F(nameof(PluginOptions.PreferExistingFiles)),
                    H("OCR (experimental)"),
                    F(nameof(PluginOptions.OcrPictureSubtitles)),
                    F(nameof(PluginOptions.OcrExistingFile)),
                    F(nameof(PluginOptions.OfferOcrInSubtitleSearch))
                }
            },
            new TabLayout
            {
                Id = SettingsTabId.Backups, Name = "Backups", Entries = new[]
                {
                    H("Backups"),
                    F(nameof(PluginOptions.BackupMode)),
                    F(nameof(PluginOptions.BackupFolder)),
                    F(nameof(PluginOptions.BackupRetentionDays)),
                    H("Restore"),
                    W(RestoreWidget)
                }
            },
            new TabLayout
            {
                Id = SettingsTabId.Logs, Name = "Logs", Entries = new[]
                {
                    W(LogsWidget)
                }
            }
        };

        /// <summary>Every setting on the page, in page order.</summary>
        internal static IEnumerable<string> FieldNames =>
            Tabs.SelectMany(t => t.Entries).Where(e => e.Kind == Field).Select(e => e.Value);

        internal static PropertyInfo Property(string name) =>
            typeof(PluginOptions).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        /// <summary>How the page draws one setting.</summary>
        internal static FieldDto Describe(string name)
        {
            var p = Property(name) ?? throw new ArgumentException("No setting called " + name);
            var dto = new FieldDto
            {
                Name = name,
                Label = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? name,
                Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                Min = p.GetCustomAttribute<MinValueAttribute>()?.MinValue,
                Max = p.GetCustomAttribute<MaxValueAttribute>()?.MaxValue
            };

            var source = p.GetCustomAttribute<SelectItemsSourceAttribute>()?.SourcePropertyName;
            if (p.PropertyType == typeof(bool)) dto.Kind = "bool";
            else if (p.PropertyType == typeof(int)) dto.Kind = "int";
            else if (p.PropertyType.IsEnum)
            {
                dto.Kind = "select";
                dto.Options = Enum.GetNames(p.PropertyType).Select(v => new OptionDto { Value = v, Name = Words(v) }).ToList();
            }
            else if (source != null)
            {
                dto.Kind = p.GetCustomAttribute<EditMultilSelectAttribute>() != null ? "multiselect" : "select";
                dto.Source = source;
            }
            else if (p.GetCustomAttribute<EditFolderPickerAttribute>() != null) dto.Kind = "folder";
            else if (p.GetCustomAttribute<EditMultilineAttribute>() is EditMultilineAttribute lines)
            {
                dto.Kind = "multiline";
                dto.Lines = lines.LineCount;
            }
            else dto.Kind = "text";

            if (p.GetCustomAttribute<VisibleConditionAttribute>() is VisibleConditionAttribute visible)
            {
                dto.VisibleIf = new ConditionDto
                {
                    Field = visible.TargetProperty,
                    Is = visible.SimpleCondition?.ToString() ?? visible.ValueCondition?.ToString(),
                    Value = visible.Value?.ToString()
                };
            }
            return dto;
        }

        /// <summary>"NextToOriginal" → "Next to original".</summary>
        internal static string Words(string name)
        {
            var words = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            return char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        /// <summary>Every setting's value as text, as the page edits it.</summary>
        internal static Dictionary<string, string> Values(PluginOptions options) =>
            FieldNames.ToDictionary(n => n, n => Format(Property(n).GetValue(options)));

        private static string Format(object value) =>
            value is bool b ? (b ? "true" : "false")
            : value is int i ? i.ToString(CultureInfo.InvariantCulture)
            : value?.ToString() ?? "";

        /// <summary>Puts the page's values into the settings. Names the page doesn't show are ignored.</summary>
        /// <returns>What's wrong with a value, or null.</returns>
        internal static string Apply(PluginOptions options, IDictionary<string, string> values)
        {
            if (values == null) return null;
            var shown = new HashSet<string>(FieldNames);
            foreach (var pair in values)
            {
                if (!shown.Contains(pair.Key)) continue;
                var p = Property(pair.Key);
                var label = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name;
                var text = pair.Value ?? "";
                if (p.PropertyType == typeof(bool))
                {
                    if (!bool.TryParse(text, out var b)) return $"'{label}' must be on or off.";
                    p.SetValue(options, b);
                }
                else if (p.PropertyType == typeof(int))
                {
                    if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return $"'{label}' must be a whole number.";
                    var min = p.GetCustomAttribute<MinValueAttribute>()?.MinValue;
                    var max = p.GetCustomAttribute<MaxValueAttribute>()?.MaxValue;
                    if (n < min || n > max) return $"'{label}' must be between {min} and {max}.";
                    p.SetValue(options, n);
                }
                else if (p.PropertyType.IsEnum)
                {
                    if (!Enum.GetNames(p.PropertyType).Contains(text)) return $"'{label}' has no choice '{text}'.";
                    p.SetValue(options, Enum.Parse(p.PropertyType, text));
                }
                else p.SetValue(options, text.Trim());
            }

            // The default can only be a language that's kept.
            if (!string.IsNullOrEmpty(options.DefaultLanguage) &&
                Plugin.DefaultLanguageChoicesFor(options.KeepLanguages).All(c => c.Value != options.DefaultLanguage))
                options.DefaultLanguage = string.Empty;
            return null;
        }

        internal static List<TabDto> Page() => Tabs.Select(t => new TabDto
        {
            Id = t.Id.ToString(),
            Name = t.Name,
            Entries = t.Entries.Select(e => new EntryDto
            {
                Kind = e.Kind,
                Text = e.Kind == Field ? null : e.Value,
                Field = e.Kind == Field ? Describe(e.Value) : null
            }).ToList(),
            Help = SettingsHelp.For(t.Id).Select(h => new HelpDto { Setting = h.Setting, Text = h.Text }).ToList()
        }).ToList();
    }

    public class TabDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<EntryDto> Entries { get; set; }
        public List<HelpDto> Help { get; set; }
    }

    public class EntryDto
    {
        public string Kind { get; set; }
        public string Text { get; set; }
        public FieldDto Field { get; set; }
    }

    public class FieldDto
    {
        public string Name { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string Kind { get; set; }
        public int? Lines { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public string Source { get; set; }
        public List<OptionDto> Options { get; set; }
        public ConditionDto VisibleIf { get; set; }
    }

    public class ConditionDto
    {
        public string Field { get; set; }
        public string Is { get; set; }
        public string Value { get; set; }
    }

    public class OptionDto
    {
        public string Value { get; set; }
        public string Name { get; set; }
    }

    public class HelpDto
    {
        public string Setting { get; set; }
        public string Text { get; set; }
    }
}
