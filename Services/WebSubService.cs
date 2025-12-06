using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MeTubeServer.Models;

namespace MeTubeServer.Services;

public class WebSubService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebSubService> _logger;
    private readonly YouTubeOptions _youtubeOptions;

    public WebSubService(
        HttpClient httpClient,
        IOptions<YouTubeOptions> options,
        ILogger<WebSubService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _youtubeOptions = options.Value;
    }

    public async Task<bool> SubscribeAsync(string topicUrl, string hubSecret, CancellationToken cancellationToken = default)
    {
        try
        {
            var callbackUrl = $"{_youtubeOptions.CallbackBaseUrl}/websub/youtube";
            
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "hub.mode", "subscribe" },
                { "hub.topic", topicUrl },
                { "hub.callback", callbackUrl },
                { "hub.secret", hubSecret }
            });

            var response = await _httpClient.PostAsync(_youtubeOptions.HubUrl, content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully subscribed to topic: {TopicUrl}", topicUrl);
                return true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to subscribe to topic {TopicUrl}. Status: {StatusCode}, Body: {Body}",
                    topicUrl, response.StatusCode, body);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to topic {TopicUrl}", topicUrl);
            return false;
        }
    }

    public async Task<bool> UnsubscribeAsync(string topicUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var callbackUrl = $"{_youtubeOptions.CallbackBaseUrl}/websub/youtube";
            
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "hub.mode", "unsubscribe" },
                { "hub.topic", topicUrl },
                { "hub.callback", callbackUrl }
            });

            var response = await _httpClient.PostAsync(_youtubeOptions.HubUrl, content, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully unsubscribed from topic: {TopicUrl}", topicUrl);
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to unsubscribe from topic {TopicUrl}. Status: {StatusCode}",
                    topicUrl, response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubscribing from topic {TopicUrl}", topicUrl);
            return false;
        }
    }

    public string GenerateHubSecret(int length = 32)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));
    }

    public bool VerifySignature(string payload, string signature, string secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret))
            return false;

        try
        {
            // Signature format: sha1=<hex_hmac> or sha256=<hex_hmac>
            var parts = signature.Split('=', 2);
            if (parts.Length != 2)
                return false;

            var algorithm = parts[0].ToLowerInvariant();
            var providedHmac = parts[1];

            byte[] computedHash;
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            if (algorithm == "sha1")
            {
                using var hmac = new HMACSHA1(keyBytes);
                computedHash = hmac.ComputeHash(payloadBytes);
            }
            else if (algorithm == "sha256")
            {
                using var hmac = new HMACSHA256(keyBytes);
                computedHash = hmac.ComputeHash(payloadBytes);
            }
            else
            {
                _logger.LogWarning("Unsupported HMAC algorithm: {Algorithm}", algorithm);
                return false;
            }

            var computedHmac = BitConverter.ToString(computedHash).Replace("-", "").ToLowerInvariant();
            return computedHmac == providedHmac.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying HMAC signature");
            return false;
        }
    }
}
