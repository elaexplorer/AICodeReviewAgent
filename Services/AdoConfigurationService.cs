using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Services;

/// <summary>
/// Service to manage Azure DevOps configuration (PAT and Organization) dynamically
/// </summary>
public class AdoConfigurationService
{
    private readonly ILogger<AdoConfigurationService> _logger;
    private string? _personalAccessToken;
    private string? _organization;
    private bool _isConfigured;

    public AdoConfigurationService(ILogger<AdoConfigurationService> logger)
    {
        _logger = logger;
        _isConfigured = false;

        // Try to load from environment variables as fallback
        _organization = Environment.GetEnvironmentVariable("ADO_ORGANIZATION");
        _personalAccessToken = Environment.GetEnvironmentVariable("ADO_PAT");

        if (!string.IsNullOrEmpty(_organization) && !string.IsNullOrEmpty(_personalAccessToken))
        {
            _isConfigured = true;
            _logger.LogInformation("ADO configuration loaded from environment variables");
        }
    }

    public bool IsConfigured => _isConfigured;
    public string? Organization => _organization;
    public string? PersonalAccessToken => _personalAccessToken;

    /// <summary>
    /// Validates the PAT with Azure DevOps and stores configuration if valid
    /// </summary>
    public async Task<(bool isValid, string? errorMessage)> ValidateAndConfigureAsync(string organization, string pat)
    {
        try
        {
            _logger.LogInformation("Validating PAT for organization: {Organization}", organization);

            // Test the PAT by making a simple API call
            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri($"https://dev.azure.com/{organization}/");
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Try to get projects to validate the PAT
            var response = await httpClient.GetAsync("_apis/projects?api-version=7.1");

            if (response.IsSuccessStatusCode)
            {
                _organization = organization;
                _personalAccessToken = pat;
                _isConfigured = true;
                _logger.LogInformation("PAT validated successfully for organization: {Organization}", organization);
                return (true, null);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("PAT validation failed: Unauthorized");
                return (false, "Invalid Personal Access Token or insufficient permissions. Please ensure your PAT has 'Code (Read)' permissions.");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("PAT validation failed: Organization not found");
                return (false, $"Organization '{organization}' not found. Please check the organization name.");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("PAT validation failed with status {Status}: {Error}", response.StatusCode, errorContent);
                return (false, $"Failed to validate PAT: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating PAT");
            return (false, $"Error validating PAT: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the current configuration
    /// </summary>
    public void ClearConfiguration()
    {
        _organization = null;
        _personalAccessToken = null;
        _isConfigured = false;
        _logger.LogInformation("ADO configuration cleared");
    }
}
