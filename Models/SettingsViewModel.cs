namespace DxpContentTransfer.Models;

public class SettingsViewModel
{
    public string IntegrationBaseUrl { get; set; }
    public string IntegrationClientKey { get; set; }
    public string IntegrationClientSecret { get; set; }

    public string PreproductionBaseUrl { get; set; }
    public string PreproductionClientKey { get; set; }
    public string PreproductionClientSecret { get; set; }

    public string ProductionBaseUrl { get; set; }
    public string ProductionClientKey { get; set; }
    public string ProductionClientSecret { get; set; }

    // Environment-indicator badge settings.
    public string IntegrationColor { get; set; }
    public string IntegrationLabel { get; set; }
    public string PreproductionColor { get; set; }
    public string PreproductionLabel { get; set; }
    public string ProductionColor { get; set; }
    public string ProductionLabel { get; set; }
    public string Selector { get; set; }
    public bool ShowOnProduction { get; set; } = true;

    public bool Saved { get; set; }
    public string ErrorMessage { get; set; }
}
