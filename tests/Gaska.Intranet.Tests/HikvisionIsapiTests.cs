using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Cameras;

namespace Gaska.Intranet.Tests;

public sealed class HikvisionIsapiTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    // Anonymised CMSearchResult as returned by a Hikvision camera recording to network storage.
    private const string SearchResult = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CMSearchResult version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
          <searchID>{C0A80001-0000-0000-0000-000000000001}</searchID>
          <responseStatus>true</responseStatus>
          <responseStatusStrg>MORE</responseStatusStrg>
          <numOfMatches>2</numOfMatches>
          <matchList>
            <searchMatchItem>
              <sourceID>{0000-0000}</sourceID>
              <trackID>101</trackID>
              <timeSpan>
                <startTime>2026-11-24T08:00:00Z</startTime>
                <endTime>2026-11-24T08:15:30Z</endTime>
              </timeSpan>
              <mediaSegmentDescriptor>
                <contentType>video</contentType>
                <codecType>H.264-BP</codecType>
                <playbackURI>rtsp://camera/Streaming/tracks/101/?starttime=20261124T080000Z&amp;endtime=20261124T081530Z&amp;name=00000000001&amp;size=1</playbackURI>
              </mediaSegmentDescriptor>
            </searchMatchItem>
            <searchMatchItem>
              <timeSpan>
                <startTime>2026-11-24T08:15:30Z</startTime>
                <endTime>2026-11-24T08:40:00Z</endTime>
              </timeSpan>
            </searchMatchItem>
          </matchList>
        </CMSearchResult>
        """;

    [Fact]
    public void Search_result_is_read_as_camera_wall_clock()
    {
        var page = HikvisionIsapi.ParseSearchResult(SearchResult, Warsaw);

        Assert.True(page.HasMore);
        Assert.Equal(2, page.Recordings.Count);
        // The "Z" is a Hikvision quirk – 08:00 is local (CET, UTC+1 in November).
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.FromHours(1)), page.Recordings[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 7, 15, 30, TimeSpan.Zero), page.Recordings[0].End.ToUniversalTime());
    }

    [Fact]
    public void No_matches_is_an_empty_last_page()
    {
        var page = HikvisionIsapi.ParseSearchResult("""
            <CMSearchResult xmlns="http://www.hikvision.com/ver10/XMLSchema">
              <responseStatusStrg>NO MATCHES</responseStatusStrg>
            </CMSearchResult>
            """, Warsaw);

        Assert.False(page.HasMore);
        Assert.Empty(page.Recordings);
    }

    [Fact]
    public void Touching_fragments_are_merged()
    {
        var page = HikvisionIsapi.ParseSearchResult(SearchResult, Warsaw);
        var merged = HikvisionIsapi.Merge(page.Recordings, TimeSpan.FromSeconds(5));

        var single = Assert.Single(merged);
        Assert.Equal(page.Recordings[0].Start, single.Start);
        Assert.Equal(page.Recordings[1].End, single.End);
    }

    [Fact]
    public void Search_request_uses_track_and_local_times()
    {
        var from = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(2));
        var xml = HikvisionIsapi.BuildSearchRequest(2, from, from.AddDays(1), 40, Warsaw, Guid.Empty);

        Assert.Contains("<trackID>201</trackID>", xml);
        Assert.Contains("<startTime>2026-07-01T00:00:00Z</startTime>", xml);
        Assert.Contains("<endTime>2026-07-02T00:00:00Z</endTime>", xml);
        Assert.Contains("<searchResultPostion>40</searchResultPostion>", xml);
    }

    [Fact]
    public void Playback_url_has_rtsp_times_and_escaped_credentials()
    {
        var camera = new CameraDefinition { Id = "x", Host = "camera.test", RtspPort = 554, Channel = 1 };
        var start = new DateTimeOffset(2026, 11, 24, 7, 0, 0, TimeSpan.Zero); // 08:00 in Warsaw

        var url = HikvisionIsapi.PlaybackRtspUrl(camera, start, start.AddHours(1), "viewer", "p@ss:word", Warsaw);

        Assert.Equal("rtsp://viewer:p%40ss%3Aword@camera.test:554/Streaming/tracks/101/?starttime=20261124T080000Z&endtime=20261124T090000Z", url);
    }

    [Fact]
    public void Live_url_picks_sub_stream_when_available()
    {
        var camera = new CameraDefinition { Id = "x", Host = "camera.test", Channel = 3 };

        Assert.EndsWith("/Streaming/Channels/302", HikvisionIsapi.LiveRtspUrl(camera, CameraStreamQuality.Sub, "", ""));
        Assert.EndsWith("/Streaming/Channels/301", HikvisionIsapi.LiveRtspUrl(camera, CameraStreamQuality.Main, "", ""));
        Assert.StartsWith("rtsp://camera.test:554/", HikvisionIsapi.LiveRtspUrl(camera, CameraStreamQuality.Main, "", ""));

        camera.HasSubStream = false;
        Assert.EndsWith("/Streaming/Channels/301", HikvisionIsapi.LiveRtspUrl(camera, CameraStreamQuality.Sub, "", ""));
    }

    [Fact]
    public void Utc_device_times_are_read_as_utc()
    {
        var utc = new CameraOptions().ResolveTimeZone();
        var page = HikvisionIsapi.ParseSearchResult(SearchResult, utc);

        Assert.Equal(TimeZoneInfo.Utc, utc);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero), page.Recordings[0].Start);
        Assert.Equal("20261124T080000Z", HikvisionIsapi.FormatRtspTime(page.Recordings[0].Start, utc));
    }

    [Fact]
    public void Relay_adds_range_only_to_play_without_one()
    {
        const string nl = "\r\n";
        const string range = "clock=20261124T094258Z-";
        const string play = "PLAY rtsp://127.0.0.1:5000/Streaming/tracks/101/ RTSP/1.0" + nl + "CSeq: 5" + nl + "Session: 1";
        const string setup = "SETUP x RTSP/1.0" + nl + "CSeq: 3";

        Assert.Equal(play + nl + "Range: " + range, RtspRangeRelay.AddRange(play, range));
        Assert.Equal(play + nl + "Range: npt=0-", RtspRangeRelay.AddRange(play + nl + "Range: npt=0-", range));
        Assert.Equal(setup, RtspRangeRelay.AddRange(setup, range));
    }
}
