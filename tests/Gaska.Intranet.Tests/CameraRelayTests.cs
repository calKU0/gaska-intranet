using System.Text;
using Gaska.Intranet.Infrastructure.Cameras;

namespace Gaska.Intranet.Tests;

public sealed class CameraRelayTests
{
    private const string Nl = "\r\n";

    [Fact]
    public void Speed_change_reuses_the_play_request_without_range()
    {
        var play = "PLAY rtsp://127.0.0.1:5000/Streaming/tracks/101/?starttime=20261124T100000Z RTSP/1.0" + Nl +
                   "CSeq: 6" + Nl + "Session: 12345" + Nl + "Authorization: Digest username=\"u\", response=\"x\"" + Nl +
                   "Range: clock=20261124T100000Z-" + Nl + "Scale: 1.0";

        var request = RtspRangeRelay.SpeedRequest(play, 4, 90001);

        Assert.StartsWith("PLAY rtsp://127.0.0.1:5000/Streaming/tracks/101/?starttime=20261124T100000Z RTSP/1.0", request);
        Assert.Contains("CSeq: 90001", request);
        Assert.Contains("Session: 12345", request);
        Assert.Contains("Authorization: Digest", request);
        Assert.DoesNotContain("Range:", request);
        Assert.EndsWith(Nl + "Scale: 4.0", request);
        Assert.Equal(1, request.Split("Scale:").Length - 1);
    }

    [Fact]
    public void Messages_are_split_into_frames_and_rtsp_texts()
    {
        var frame = new byte[] { (byte)'$', 0, 0, 3, 1, 2, 3 };
        var response = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK" + Nl + "CSeq: 90002" + Nl + "Content-Length: 2" + Nl + Nl + "ok");
        var data = frame.Concat(response).ToArray();

        var first = RtspRangeRelay.TryTakeMessage(data, out var frameMessage);
        Assert.Equal(frame.Length, first);
        Assert.Null(frameMessage.Header);
        Assert.Equal(frame, frameMessage.Frame.ToArray());

        var second = RtspRangeRelay.TryTakeMessage(data.AsSpan(first), out var textMessage);
        Assert.Equal(response.Length, second);
        Assert.Equal(90002, RtspRangeRelay.CSeqOf(textMessage.Header!));
        Assert.Equal("ok", Encoding.ASCII.GetString(textMessage.Body.Span));

        // Incomplete data waits for more.
        Assert.Equal(0, RtspRangeRelay.TryTakeMessage(data.AsSpan(0, 5), out _));
    }
}
