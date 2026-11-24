using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Cameras;

namespace Gaska.Intranet.Tests;

public sealed class CameraPlaybackTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.FromHours(1));

    // Motion recording: 10:00:00–10:00:30, 10:05:00–10:06:00, 11:00:00–11:00:20.
    private static readonly IReadOnlyList<CameraRecording> Recordings =
    [
        new(Day.AddHours(10), Day.AddHours(10).AddSeconds(30)),
        new(Day.AddHours(10).AddMinutes(5), Day.AddHours(10).AddMinutes(6)),
        new(Day.AddHours(11), Day.AddHours(11).AddSeconds(20)),
    ];

    [Fact]
    public void Position_runs_through_the_recordings_and_skips_the_gaps()
    {
        var start = Day.AddHours(10).AddSeconds(10);

        Assert.Equal(Day.AddHours(10).AddSeconds(25), CameraPlaybackSpeeds.PositionAt(start, 15, Recordings));
        // 20 s left in the first recording, then 10 s into the second.
        Assert.Equal(Day.AddHours(10).AddMinutes(5).AddSeconds(10), CameraPlaybackSpeeds.PositionAt(start, 30, Recordings));
        // Past the second recording (20 + 60 s) into the third.
        Assert.Equal(Day.AddHours(11).AddSeconds(5), CameraPlaybackSpeeds.PositionAt(start, 85, Recordings));
    }

    [Fact]
    public void Playback_starts_in_the_recording_or_at_the_next_one()
    {
        Assert.Equal(Day.AddHours(10).AddSeconds(10), CameraPlaybackSpeeds.StartAt(Day.AddHours(10).AddSeconds(10), Recordings));
        Assert.Equal(Day.AddHours(10).AddMinutes(5), CameraPlaybackSpeeds.StartAt(Day.AddHours(10).AddMinutes(1), Recordings));
        Assert.Null(CameraPlaybackSpeeds.StartAt(Day.AddHours(12), Recordings));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(8, 8)]
    [InlineData(3, 1)]
    [InlineData(0, 1)]
    public void Only_offered_speeds_are_used(int requested, int expected) =>
        Assert.Equal(expected, CameraPlaybackSpeeds.Normalize(requested));

    [Fact]
    public void Faster_playback_asks_the_camera_to_scale()
    {
        const string nl = "\r\n";
        const string play = "PLAY rtsp://127.0.0.1:5000/Streaming/tracks/101/ RTSP/1.0" + nl + "CSeq: 5";

        Assert.Equal(play + nl + "Range: clock=20261124T094258Z-" + nl + "Scale: 4.0",
            RtspRangeRelay.AddRange(play, "clock=20261124T094258Z-", 4));
        Assert.Equal(play + nl + "Range: clock=20261124T094258Z-", RtspRangeRelay.AddRange(play, "clock=20261124T094258Z-", 1));
    }

    [Fact]
    public void Download_files_join_into_one_stream()
    {
        Assert.True(HikvisionIsapi.HasFileHeader("IMKH"u8.ToArray().Concat(new byte[36]).ToArray()));
        Assert.False(HikvisionIsapi.HasFileHeader(new byte[] { 0, 0, 1, 0xBA }));
        Assert.Contains("<playbackURI>rtsp://x/Streaming/tracks/101/?starttime=1&amp;name=a</playbackURI>",
            HikvisionIsapi.BuildDownloadRequest("rtsp://x/Streaming/tracks/101/?starttime=1&name=a"));
    }

    [Fact]
    public void Search_result_keeps_the_file_uri()
    {
        const string xml = """
            <CMSearchResult xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <responseStatusStrg>OK</responseStatusStrg>
              <matchList><searchMatchItem>
                <timeSpan><startTime>2026-11-24T09:00:00Z</startTime><endTime>2026-11-24T09:00:30Z</endTime></timeSpan>
                <mediaSegmentDescriptor><playbackURI>rtsp://camera/Streaming/tracks/101/?starttime=20261124T090000Z&amp;name=f1&amp;size=9</playbackURI></mediaSegmentDescriptor>
              </searchMatchItem></matchList>
            </CMSearchResult>
            """;

        var file = Assert.Single(HikvisionIsapi.ParseSearchResult(xml, TimeZoneInfo.Utc).Files);
        Assert.Equal("rtsp://camera/Streaming/tracks/101/?starttime=20261124T090000Z&name=f1&size=9", file.PlaybackUri);
    }
}
