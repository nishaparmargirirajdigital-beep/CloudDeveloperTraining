using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Scoping;

namespace UmbracoProject.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class CloudDeployWebhookController : ControllerBase
    {
        private readonly IContentService _contentService;
        private readonly IConfiguration _config;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly ILogger<CloudDeployWebhookController> _logger;
        public CloudDeployWebhookController(
            IContentService contentService,
            IConfiguration config,
            ICoreScopeProvider scopeProvider,
            ILogger<CloudDeployWebhookController> logger)
        {
            _contentService = contentService;
            _config = config;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        [HttpGet("cloud-deploy")]
        [HttpPost("cloud-deploy")]
        public IActionResult ReceiveFromCloudDeploy(
            [FromBody] JsonElement payload,
            [FromQuery(Name = "t")] string? token = null)
        {
            return HandleWebhook(payload, token);
        }

        private IActionResult HandleWebhook(JsonElement payload, string? token)
        {
            var section = _config.GetSection("DeploymentWebhook");

            var secret = section["Secret"] ?? string.Empty;
            _logger.LogInformation("Webhook called. Token: {Token}, SecretConfigured: {HasSecret}", token, !string.IsNullOrEmpty(secret));

            if (string.IsNullOrWhiteSpace(token) ||
                !string.Equals(token, secret, StringComparison.Ordinal))
            {
                _logger.LogWarning("Unauthorized webhook call. Token mismatch.");
                return Unauthorized();
            }

            if (!Guid.TryParse(section["ContentKey"], out var contentKey))
            {
                _logger.LogError("Invalid ContentKey in configuration: {Key}", section["ContentKey"]);
                return BadRequest("Invalid ContentKey.");
            }

            var propAlias = section["PropertyAlias"] ?? "deploymentData";
            var content = _contentService.GetById(contentKey);

            if (content is null)
            {
                _logger.LogError("Content not found for key {Key}", contentKey);
                return NotFound();
            }

            using (var scope = _scopeProvider.CreateCoreScope())
            {
                content.SetValue(propAlias, payload.GetRawText());
                _contentService.Save(content);

                var cultures = content.ContentType.VariesByCulture()
                    ? (content.AvailableCultures ?? Enumerable.Empty<string>()).ToArray()
                    : Array.Empty<string>();

                var publishResult = _contentService.Publish(content, cultures, -1);
                scope.Complete();

                if (!publishResult.Success)
                    return StatusCode(500, "Publish failed.");
            }

            return Ok(new { ok = true });
        }
    }
}