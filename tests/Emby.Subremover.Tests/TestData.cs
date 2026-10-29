using System.Text.Json;
using Emby.Subremover.Core;

namespace Emby.Subremover.Tests;

internal static class TestData
{
    public static ProbeStream Sub(int index, string lang, string codec = "subrip", bool forced = false, bool sdh = false, string title = null,
        bool isDefault = false)
    {
        var tags = new Dictionary<string, string>();
        if (lang != null) tags["language"] = lang;
        if (title != null) tags["title"] = title;
        return new ProbeStream
        {
            index = index,
            codec_type = "subtitle",
            codec_name = codec,
            tags = tags,
            disposition = new Dictionary<string, int>
            {
                ["default"] = isDefault ? 1 : 0, ["forced"] = forced ? 1 : 0, ["hearing_impaired"] = sdh ? 1 : 0
            }
        };
    }

    public static ProbeResult Movie(params ProbeStream[] subs)
    {
        var result = new ProbeResult { format = new ProbeFormat { duration = "5400.000000" } };
        result.streams.Add(new ProbeStream { index = 0, codec_type = "video", codec_name = "h264" });
        result.streams.Add(new ProbeStream { index = 1, codec_type = "audio", codec_name = "eac3" });
        result.streams.AddRange(subs);
        return result;
    }

    public static ProbeResult Parse(string ffprobeJson) => JsonSerializer.Deserialize<ProbeResult>(ffprobeJson);
}
