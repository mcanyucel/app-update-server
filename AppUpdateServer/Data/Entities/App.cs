namespace AppUpdateServer.Data.Entities;

public class App
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Optional grouping label (e.g. a company or product line). Lets a consumer
    /// such as an intranet download page list only its own apps via the public
    /// catalog endpoint. Stored normalised — see UpdateService.NormalizeTag.
    /// </summary>
    public string? Tag { get; set; }
    public ICollection<AppVersion> Versions { get; set; } = [];
}
