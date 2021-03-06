using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Storage.Blob;
using System.Web;
using System.Linq;

namespace ExpiredSasTokenHandling
{
    // StorageAccountConnectionString should be a key in your Function App Configuration (or local.settings.json for local development)
    // It's value should contain a connection string to your Storage Account.
    [StorageAccount("StorageAccountConnectionString")]
    public static class ExpiredSasTokenHandling
    {
        private const string ContainerName = "YOUR_CONTAINER_NAME";
        private const string FilePath = "/PATH/TO/YOUR/FILE.pdf";
        private const string FullFilePath = ContainerName + FilePath;

        [FunctionName("generateFileLink")]
        public static IActionResult GenerateFileLink(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]
            HttpRequest request, [Blob(FullFilePath)] CloudBlobContainer container)
        {
            var accessPolicy = new SharedAccessBlobPolicy()
            {
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(1),
                Permissions = SharedAccessBlobPermissions.Read
            };

            var sasToken = container.GetSharedAccessSignature(accessPolicy);
            var authenticatedBlobLink = new Uri(container.Uri + FilePath + sasToken);

            var linkToFriendlyProxy = new UriBuilder
            {
                Scheme = request.Scheme,
                Host = request.Host.Host,
                Port = request.Host.Port.GetValueOrDefault(80),
                Path = $"api/{FileProxyFunctionName}",
            };

            var query = HttpUtility.ParseQueryString(linkToFriendlyProxy.Query);
            query[FileProxyUrlParameter] = authenticatedBlobLink.ToString();
            linkToFriendlyProxy.Query = query.ToString();

            return new OkObjectResult(linkToFriendlyProxy.ToString());
        }

        private const string FileProxyFunctionName = "fileProxy";
        private const string FileProxyUrlParameter = "originalUrl";
        private const string StorageHost = "YOUR_STORAGE_ACCOUNT_NAME.blob.core.windows.net";

        [FunctionName(FileProxyFunctionName)]
        public static IActionResult FileProxy(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]
            HttpRequest request)
        {
            var blobUrlWithToken = request.Query.ContainsKey(FileProxyUrlParameter)
                ? request.Query[FileProxyUrlParameter][0]
                : null;

            if (!Uri.TryCreate(blobUrlWithToken, UriKind.Absolute, out var blobUrl))
            {
                return GetInvalidLinkHtml();
            }

            // You should only redirect to your own resources for safety (more info - 'Open redirect vulnerability')
            if (!string.Equals(blobUrl.Host, StorageHost, StringComparison.InvariantCultureIgnoreCase))
            {
                return GetInvalidLinkHtml();
            }

            // Show error if token's validity date is not there or is in the past
            var validityDateParameterValue = HttpUtility.ParseQueryString(blobUrl.Query).GetValues("se");
            if (validityDateParameterValue?.Any() != true ||
                !DateTime.TryParse(validityDateParameterValue.First(), out var validityDateTime) ||
                DateTime.UtcNow > validityDateTime.ToUniversalTime())
            {
                return GetInvalidLinkHtml();
            }

            return new RedirectResult(blobUrlWithToken);
        }

        private static IActionResult GetInvalidLinkHtml() => new ContentResult()
        {
            // Keep calm, it's just HTML. You can consider to use a library to build it safer (for example HtmlGenerator)
            Content =
                $"<html><body><div style=\"text-align: center; margin: 5%; margin-top: 20%\"><p style=\"font-size: 4vh; font-family:'San Francisco'\">This link is not valid anymore, please go back to the app and regenerate it.</p></div></body></html>",
            ContentType = "text/html"
        };
    }
}