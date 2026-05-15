namespace AppUpdateServer.Data.Entities;

public class App
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public ICollection<AppVersion> Versions { get; set; } = [];
}
