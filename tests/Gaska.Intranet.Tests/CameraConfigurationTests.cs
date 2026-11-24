using System.Text;
using Gaska.Intranet.Core.Cameras;
using Gaska.Intranet.Infrastructure.Cameras;
using Microsoft.Extensions.Configuration;

namespace Gaska.Intranet.Tests;

public sealed class CameraConfigurationTests
{
    [Theory]
    [InlineData("Recepcja", "IP CAMERA", "Recepcja")]
    [InlineData("Parking_biuro#2", "IP CAMERA", "Parking biuro#2")]
    [InlineData("IP CAMERA", "Biuro Recepcja", "Biuro Recepcja")]
    [InlineData("Camera 01", "Stanowisko_1116", "Stanowisko 1116")]
    [InlineData("Camera 01?", "IP CAMERA", null)]
    [InlineData("Camera 01", "IP CAMERA 2", null)]
    [InlineData(null, null, null)]
    public void Camera_name_skips_factory_defaults(string? deviceName, string? osdName, string? expected) =>
        Assert.Equal(expected, HikvisionIsapi.PickName(deviceName, osdName));

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
              { "Id": "rampa", "Host": "10.0.0.4", "Location": "WarehouseA", "Name": "Rampa" }
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
    [InlineData("Biuro Wej'scie Tyl", "Biuro Wejście Tył")]
    [InlineData("Biuro Ksiegowo's'c", "Biuro Księgowość")]
    [InlineData("Sklep 'Scianka", "Sklep Ścianka")]
    [InlineData("Antresola Mag. A 1 Pietro #2", "Antresola Mag. A 1 Piętro #2")]
    [InlineData("Pakowanie Dól Mag. A", "Pakowanie Dół Mag. A")]
    [InlineData("PRZYJECIA 2", "PRZYJĘCIA 2")]
    [InlineData("Rampa Mag. B", "Rampa Mag. B")]
    public void Camera_names_are_shown_with_polish_letters(string fromCamera, string expected) =>
        Assert.Equal(expected, HikvisionIsapi.ToPolish(fromCamera, new CameraOptions().PolishWords));

    [Fact]
    public void Configured_words_are_added_to_the_defaults()
    {
        const string json = """{ "Cameras": { "PolishWords": { "Lakiernia": "Lakiernia główna" } } }""";
        var options = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build()
            .GetSection(CameraOptions.SectionName).Get<CameraOptions>()!;

        Assert.Equal("Lakiernia główna Tył", HikvisionIsapi.ToPolish("Lakiernia Tyl", options.PolishWords));
    }

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
