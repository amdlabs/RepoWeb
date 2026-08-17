using IVZVision.Core.Configuration;
using Xunit;

namespace IVZVision.Tests;

public class ConfigurationTests
{
    [Theory]
    // Hikvision codifica canal y perfil como canal*100 + perfil.
    [InlineData(1, StreamProfile.Main, "rtsp://admin:clave@192.168.1.64:554/Streaming/Channels/101")]
    [InlineData(1, StreamProfile.Sub, "rtsp://admin:clave@192.168.1.64:554/Streaming/Channels/102")]
    [InlineData(2, StreamProfile.Main, "rtsp://admin:clave@192.168.1.64:554/Streaming/Channels/201")]
    [InlineData(12, StreamProfile.Third, "rtsp://admin:clave@192.168.1.64:554/Streaming/Channels/1203")]
    public void Url_Hikvision_Usa_El_Formato_De_Canal_Correcto(int channel, StreamProfile stream, string expected)
    {
        var camera = NewCamera();
        camera.Channel = channel;
        camera.Stream = stream;

        Assert.Equal(expected, camera.BuildRtspUrl());
    }

    [Theory]
    [InlineData(StreamProfile.Main, 0)]
    [InlineData(StreamProfile.Sub, 1)]
    [InlineData(StreamProfile.Third, 2)]
    public void Url_Dahua_Usa_Subtype_Empezando_En_Cero(StreamProfile stream, int subtype)
    {
        var camera = NewCamera();
        camera.Vendor = CameraVendor.Dahua;
        camera.Stream = stream;

        Assert.Equal($"rtsp://admin:clave@192.168.1.64:554/cam/realmonitor?channel=1&subtype={subtype}",
                     camera.BuildRtspUrl());
    }

    [Fact]
    public void Url_Se_Escapan_Los_Caracteres_Especiales_De_La_Clave()
    {
        var camera = NewCamera();
        camera.Password = "p@ss:word/1";

        var url = camera.BuildRtspUrl();

        // La clave va codificada para no romper el parseo de la URL.
        Assert.Contains("p%40ss%3Aword%2F1", url);
        Assert.EndsWith("@192.168.1.64:554/Streaming/Channels/102", url);
    }

    [Fact]
    public void Url_Enmascarada_No_Filtra_Credenciales()
    {
        var camera = NewCamera();

        var masked = camera.BuildRtspUrl(maskCredentials: true);

        Assert.DoesNotContain("clave", masked);
        Assert.DoesNotContain("admin", masked);
        Assert.Contains("***", masked);
    }

    [Fact]
    public void Url_Manual_Tiene_Prioridad_Y_Tambien_Se_Enmascara()
    {
        var camera = NewCamera();
        camera.RtspUrlOverride = "rtsp://usuario:secreto@10.0.0.5:8554/live";

        Assert.Equal("rtsp://usuario:secreto@10.0.0.5:8554/live", camera.BuildRtspUrl());
        Assert.Equal("rtsp://***:***@10.0.0.5:8554/live", camera.BuildRtspUrl(maskCredentials: true));
    }

    [Fact]
    public void Cadena_De_Conexion_Con_Autenticacion_Windows_No_Lleva_Usuario()
    {
        var db = new DatabaseConfig { Server = @".\SQLEXPRESS", Database = "IVZVision", IntegratedSecurity = true };

        var connectionString = db.BuildConnectionString();

        Assert.Contains(@"Data Source=.\SQLEXPRESS", connectionString);
        Assert.Contains("Initial Catalog=IVZVision", connectionString);
        Assert.Contains("Integrated Security=True", connectionString);
        Assert.DoesNotContain("User ID", connectionString);
    }

    [Fact]
    public void Cadena_De_Conexion_Con_Usuario_Sql_Incluye_Credenciales()
    {
        var db = new DatabaseConfig
        {
            Server = "192.168.1.10,1433",
            Database = "Vision",
            IntegratedSecurity = false,
            UserId = "sa",
            Password = "Clave123",
        };

        var connectionString = db.BuildConnectionString();

        Assert.Contains("User ID=sa", connectionString);
        Assert.Contains("Password=Clave123", connectionString);
    }

    [Fact]
    public void Cadena_Manual_Anula_El_Resto_De_Campos()
    {
        var db = new DatabaseConfig
        {
            Server = "ignorado",
            ConnectionStringOverride = "Server=otro;Database=Otra;Trusted_Connection=True;",
        };

        Assert.Equal("Server=otro;Database=Otra;Trusted_Connection=True;", db.BuildConnectionString());
        Assert.Equal("Otra", db.ResolveDatabaseName());
    }

    [Fact]
    public void Conexion_A_Master_Conserva_El_Servidor()
    {
        var db = new DatabaseConfig { Server = @".\SQLEXPRESS", Database = "IVZVision" };

        var master = db.BuildMasterConnectionString();

        Assert.Contains("Initial Catalog=master", master);
        Assert.Contains(@"Data Source=.\SQLEXPRESS", master);
    }

    [Fact]
    public void Clonar_La_Configuracion_Es_Una_Copia_Independiente()
    {
        var config = new AppConfig();
        config.Cameras.Add(NewCamera());

        var clone = config.Clone();
        clone.Cameras[0].Name = "Cambiada";
        clone.Database.Server = "otro";

        Assert.Equal("Puerta", config.Cameras[0].Name);
        Assert.Equal(@".\SQLEXPRESS", config.Database.Server);
    }

    private static CameraConfig NewCamera() => new()
    {
        Name = "Puerta",
        Host = "192.168.1.64",
        Username = "admin",
        Password = "clave",
        Channel = 1,
        Stream = StreamProfile.Sub,
        RtspPort = 554,
    };
}
