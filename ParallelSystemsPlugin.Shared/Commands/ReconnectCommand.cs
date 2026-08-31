using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.Configs;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal sealed class ReconnectCommand : IExternalCommand
    {
        private static readonly TimeSpan AuthorizationRequestTimeout =
            TimeSpan.FromSeconds(15);

        private static readonly HttpClient HttpClient =
            new HttpClient();

        public Result Execute(
            ExternalCommandData data,
            ref string message,
            ElementSet elements)
        {
            try
            {
                if (ParallelSystemsPlugin.App
                        .IsDevelopmentServerBypassEnabled())
                {
                    ParallelSystemsPlugin.App.IsUserAuthorized = true;

                    AppDialog.Success(
                        "Development Mode",
                        "Server authorization is bypassed for this Revit " +
                        "session. No authorization request was sent.");

                    return Result.Succeeded;
                }

                if (data == null)
                {
                    throw new InvalidOperationException(
                        "Revit command data is unavailable.");
                }

                UIApplication uiApp = data.Application;

                string username = GetCurrentUsername(uiApp);

                if (string.IsNullOrWhiteSpace(username))
                {
                    throw new InvalidOperationException(
                        "The current Revit username could not be determined.");
                }

                UserCheckResponse response =
                    CheckUserAsync(
                            username,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                /*
                 * Update these checks based on the actual properties
                 * contained in UserCheckResponse.
                 *
                 * Example:
                 *
                 * if (!response.Exists)
                 * {
                 *     AppDialog.Warning(
                 *         "Reconnect",
                 *         "The current Revit user is not registered.");
                 *
                 *     return Result.Cancelled;
                 * }
                 */

                if (response == null)
                {
                    throw new Exception("response is null");
                }
                    

                ParallelSystemsPlugin.App.IsUserAuthorized = response.Allowed;

                if (response.Allowed)
                {
                    AppDialog.Success(
                    "Reconnect Successful",
                    "Your authorization has been verified. You can now continue using the Parallel Systems plugin.");

                    return Result.Succeeded;
                }

                else
                {
                    AppDialog.Warn(
                    "Access Not Authorized",
                    $"The Revit user \"{username}\" is not authorized to use the Parallel Systems plugin. " +
                     "Please contact your administrator.");

                    return Result.Cancelled;
                }
            }
            catch (AuthorizationTimeoutException ex)
            {
                LogException(ex);

                AppDialog.Warn(
                    "Reconnect Timeout",
                    ex.Message);

                return Result.Cancelled;
            }
            catch (AuthorizationHttpException ex)
            {
                LogException(ex);

                string userMessage = ex.IsTransient
                    ? "The authorization service is temporarily unavailable. " +
                      "Please try again."
                    : "The authorization server rejected the request.";

                AppDialog.Error(
                    "Reconnect Failed",
                    userMessage);

                return Result.Cancelled;
            }
            catch (HttpRequestException ex)
            {
                LogException(ex);

                AppDialog.Error(
                    "Reconnect Failed",
                    "The authorization server could not be reached. " +
                    "Check your internet connection and try again.");

                return Result.Cancelled;
            }
            catch (JsonException ex)
            {
                LogException(ex);

                AppDialog.Error(
                    "Reconnect Failed",
                    "The authorization server returned an invalid response.");

                return Result.Cancelled;
            }
            catch (InvalidOperationException ex)
            {
                LogException(ex);

                AppDialog.Error(
                    "Reconnect Configuration Error",
                    ex.Message);

                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                LogException(ex);

                AppDialog.Error(
                    "Reconnect Failed",
                    "An unexpected error occurred while reconnecting.");

                return Result.Cancelled;
            }
        }

        private static async Task<UserCheckResponse> CheckUserAsync(
            string username,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException(
                    "The username is required.",
                    nameof(username));
            }

            if (string.IsNullOrWhiteSpace(
                    ApiConfig.ApiSettings?.BaseUrl))
            {
                throw new InvalidOperationException(
                    "The API BaseUrl is missing from the configuration.");
            }

            string baseUrl =
                ApiConfig.ApiSettings.BaseUrl
                    .Trim()
                    .TrimEnd('/');

            string encodedUsername =
                Uri.EscapeDataString(username.Trim());

            string url =
                baseUrl +
                "/api/auth/check-user/" +
                encodedUsername;

            using (CancellationTokenSource requestCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken))
            {
                requestCancellation.CancelAfter(
                    AuthorizationRequestTimeout);

                try
                {
                    using (HttpResponseMessage response =
                           await HttpClient
                               .GetAsync(
                                   url,
                                   requestCancellation.Token)
                               .ConfigureAwait(false))
                    {
                        string json =
                            await response.Content
                                .ReadAsStringAsync()
                                .ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            bool isTransient =
                                response.StatusCode ==
                                    HttpStatusCode.RequestTimeout ||
                                (int)response.StatusCode == 429 ||
                                (int)response.StatusCode >= 500;

                            throw new AuthorizationHttpException(
                                BuildHttpErrorMessage(
                                    response,
                                    json),
                                isTransient);
                        }

                        if (string.IsNullOrWhiteSpace(json))
                        {
                            throw new JsonException(
                                "The authorization server returned an empty response.");
                        }

                        UserCheckResponse result =
                            JsonConvert.DeserializeObject<UserCheckResponse>(
                                json);

                        if (result == null)
                        {
                            throw new JsonException(
                                "The authorization response could not be parsed.");
                        }

                        return result;
                    }
                }
                catch (OperationCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AuthorizationTimeoutException(
                        "The authorization request exceeded the " +
                        (int)AuthorizationRequestTimeout.TotalSeconds +
                        " second timeout.",
                        ex);
                }
            }
        }

        private static string GetCurrentUsername(
            UIApplication uiApp)
        {
            if (uiApp?.Application == null)
                return null;

            string username =
                uiApp.Application.Username;

            return string.IsNullOrWhiteSpace(username)
                ? null
                : username.Trim();
        }

        private static string BuildHttpErrorMessage(
            HttpResponseMessage response,
            string responseBody)
        {
            string reasonPhrase =
                string.IsNullOrWhiteSpace(response.ReasonPhrase)
                    ? "Unknown error"
                    : response.ReasonPhrase;

            string message =
                "Authorization failed with HTTP " +
                (int)response.StatusCode +
                " (" +
                reasonPhrase +
                ").";

            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                message +=
                    " Response: " +
                    LimitLength(responseBody, 2000);
            }

            return message;
        }

        private static string LimitLength(
            string value,
            int maximumLength)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            if (value.Length <= maximumLength)
                return value;

            return value.Substring(0, maximumLength) +
                   "...";
        }

        private static void LogException(
            Exception exception)
        {
            if (exception == null)
                return;

            // Replace this with your existing application logger.
            System.Diagnostics.Debug.WriteLine(
                exception.ToString());
        }

        private sealed class AuthorizationHttpException :
            Exception
        {
            public AuthorizationHttpException(
                string message,
                bool isTransient)
                : base(message)
            {
                IsTransient = isTransient;
            }

            public bool IsTransient { get; }
        }

        private sealed class AuthorizationTimeoutException :
            TimeoutException
        {
            public AuthorizationTimeoutException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private sealed class UserCheckResponse
        {
            public string Username { get; set; }

            public bool Allowed { get; set; }
        }
    }
}
