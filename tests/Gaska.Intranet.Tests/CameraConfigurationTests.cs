using System.Text;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Cameras;
using Microsoft.Extensions.Configuration;

namespace Gaska.Intranet.Tests;

public sealed class CameraConfigurationTests
{
    [Fact]
    public void Channel_name_is_read_from_isapi_xml()
    {
        const string xml = """
            <VideoInputChannel version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <id>1</id><inputPort>1</inputPort><name>Pakowanie 11</name>
            </VideoInputChannel>
            """;

        Assert.Equal("Pakowanie 11", HikvisionIsapi.ReadElement(xml, "name"));
        Assert.Null(HikvisionIsapi.ReadElement(xml, "deviceName"));
    }

    [Fact]
    public void Id_defaults_to_the_address()
    {
        Assert.Equal("10-0-0-43", new CameraDefinition { Host = "10.0.0.43" }.EffectiveId);
        Assert.Equal("recepcja", new CameraDefinition { Id = "recepcja", Host = "10.0.0.43" }.EffectiveId);
    }

    [Fact]
    public void Cameras_without_location_bind_and_validate()
    {
        const string json = """
            { "Cameras": { "Cameras": [
              { "Host": "10.0.0.1", "Location": null },
              { "Host": "10.0.0.2" },
              { "Host": "10.0.0.3", "Location": "Packing" },
              { "Id": "rampa", "Host": "10.0.0.4", "Location": "WarehouseA" }
            ] } }
            """;
        var options = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build()
            .GetSection(CameraOptions.SectionName).Get<CameraOptions>()!;

        Assert.Equal([null, null, CameraLocation.Packing, CameraLocation.WarehouseA], options.Cameras.Select(c => c.Location));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Same_address_twice_is_reported()
    {
        var options = new CameraOptions
        {
            Cameras = [new CameraDefinition { Host = "10.0.0.1" }, new CameraDefinition { Host = "10.0.0.1" }],
        };

        Assert.Contains(options.Validate(), e => e.Contains("more than once"));
    }

    [Fact]
    public void Packing_is_a_location_with_its_own_permission()
    {
        var packing = CameraLocations.FromSlug("pakowanie");

        Assert.NotNull(packing);
        Assert.Equal(CameraLocation.Packing, packing.Location);
        Assert.Equal("cameras.packing", packing.ModuleKey);
    }

    [Theory]
    [InlineData("Pakowanie Dol Mag. A", "Pakowanie Dol Mag. A")]
    [InlineData("IP CAMERA", "IP CAMERA")]
    [InlineData(null, "10.0.0.1")]
    public void Camera_names_are_shown_as_the_camera_keeps_them(string? device, string expected) =>
        Assert.Equal(expected, CameraCatalog.DisplayName(new CameraDefinition { Host = "10.0.0.1", DeviceName = device, OsdName = "OSD text" }));

    [Fact]
    public void Camera_can_be_in_several_tabs()
    {
        var packing = new CameraDefinition { Host = "10.0.0.175", Location = CameraLocation.Packing, AlsoIn = [CameraLocation.WarehouseA] };

        Assert.Equal([CameraLocation.Packing, CameraLocation.WarehouseA], packing.Locations);
        Assert.Equal([CameraLocation.Unassigned], new CameraDefinition { Host = "10.0.0.79" }.Locations);
        Assert.Contains(new CameraOptions { Cameras = [new CameraDefinition { Host = "x", AlsoIn = [(CameraLocation)50] }] }.Validate(),
            e => e.Contains("AlsoIn"));
    }
}
