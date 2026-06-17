namespace DxpContentTransfer.Models;

public class DxpEnvironmentConfig
{
    public string Name { get; set; }
    public string BaseUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }

    // Environment-indicator presentation (not used for transfers): the badge colour and the custom
    // label shown in the top bar for this environment.
    public string Color { get; set; }
    public string Label { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientKey) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
