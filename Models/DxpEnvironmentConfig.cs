namespace DxpContentTransfer.Models;

public class DxpEnvironmentConfig
{
    public string Name { get; set; }
    public string BaseUrl { get; set; }
    public string ClientKey { get; set; }
    public string ClientSecret { get; set; }

    // Optional friendly name shown in the gadget's transfer UI (e.g. "UAT" for the Preproduction
    // slot). Display only — Name stays the identity everywhere that acts on the environment
    // (host-matching, the API's TargetEnvironment, credential selection, logs).
    public string Label { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientKey) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
