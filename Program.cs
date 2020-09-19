using Microsoft.Extensions.Http;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Polly;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace azuredevops_rest_api
{
    static class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");

            using var cancellationTokenSource = new CancellationTokenSource();

            void cancelEventHandler(object sender, ConsoleCancelEventArgs e)
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
            Console.CancelKeyPress += cancelEventHandler;
            var cancellationToken = cancellationTokenSource.Token;


            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => {
                Console.WriteLine($"Trusting {cert.Subject}");
                return true;
            };


            string collectionUrl = args[0];
            string devOpsToken = args[1];
            int worktItemId = int.Parse(args[2]);
            TestRetry(collectionUrl, devOpsToken, worktItemId, cancellationToken).Wait();
        }

        private static async Task TestRetry(string collectionUrl, string devOpsToken, int worktItemId, CancellationToken cancellationToken)
        {
            // see https://docs.microsoft.com/en-us/azure/devops/integrate/concepts/rate-limits#api-client-experience
            int MaxRetries = 3;
            var policy = Policy
                .Handle<HttpRequestException>()
                // https://github.com/App-vNext/Polly/wiki/Retry#retryafter-when-the-response-specifies-how-long-to-wait
                .OrResult<HttpResponseMessage>(r => r.StatusCode == (HttpStatusCode)429)
                .WaitAndRetryAsync(
                    retryCount: MaxRetries,
                    sleepDurationProvider: (retryCount, response, context) => {
                        return response.Result?.Headers.RetryAfter.Delta.Value
                                ?? TimeSpan.FromSeconds(30 * retryCount);
                    },
                    onRetryAsync: async (response, timespan, retryCount, context) => {
                        await Console.Out.WriteLineAsync($"{Environment.NewLine}Waiting {timespan} before retrying (attemp #{retryCount}/{MaxRetries})...");
                    }
                );
            var handler = new PolicyHttpMessageHandler(policy);

            var clientCredentials = new VssBasicCredential("", devOpsToken);
            var vssHandler = new VssHttpMessageHandler(clientCredentials, VssClientHttpRequestSettings.Default.Clone());
            using var devops = new VssConnection(new Uri(collectionUrl), vssHandler, new DelegatingHandler[] { handler });
            await devops.ConnectAsync(cancellationToken);

            using var witClient = await devops.GetClientAsync<WorkItemTrackingHttpClient>(cancellationToken);
            for (int i = 0; i < 10; i++)
            {
                _ = await witClient.GetWorkItemAsync(worktItemId);
                Console.Write('+');
            }

            devops.Disconnect();
        }
    }
}
