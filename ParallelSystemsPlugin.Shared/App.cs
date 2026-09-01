using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Timesheets;
using ParallelSystemsPlugin.UI;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin
{
    public class App : IExternalApplication
    {
        private const string TabName = "ParallelSystems";
        private const string DevelopmentModeEnvironmentVariable =
            "PARALLEL_SYSTEMS_DEVELOPMENT_MODE";
        private const string DevelopmentModePassword = "%SupportMode100%1";

        private static bool _developmentModeChecked;
        private static bool _developmentModeEnabled;

        /*
         * Render free services can take close to a minute to wake up.
         * Keep HttpClient's global timeout disabled and enforce a timeout per
         * authorization attempt so that cancellation and retries are explicit.
         */
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly TimeSpan AuthorizationRequestTimeout =
            TimeSpan.FromSeconds(75);

        private static readonly TimeSpan AuthorizationRetryDelay =
            TimeSpan.FromSeconds(5);

        private static readonly TimeSpan AuthorizationCycleRetryDelay =
            TimeSpan.FromSeconds(20);

        private const int AuthorizationAttemptsPerCycle = 1;

        private TimesheetTracker _timesheetTracker;
        private CancellationTokenSource _authorizationCancellation;
        private Task<UserCheckResponse> _authorizationTask;
        private DateTime _nextAuthorizationAttemptUtc;
        private string _authorizationUsername;

        private bool _authorizationStarted;
        private bool _authorizationChecked;
        private bool _authorizationUnavailableDialogShown;
        private bool _isAuthorized;
        private bool _servicesStarted;

        public static bool IsUserAuthorized { get; set; }

        public static bool IsAuthorizationPending { get; private set; }

        public static string AuthorizationStatusMessage { get; private set; }

        public static bool IsDevelopmentServerBypassEnabled()
        {
            if (_developmentModeChecked)
                return _developmentModeEnabled;

            _developmentModeChecked = true;

            if (Environment.GetEnvironmentVariable(
                    DevelopmentModeEnvironmentVariable) == null)
            {
                return false;
            }

            _developmentModeEnabled = PromptForDevelopmentModePassword();
            return _developmentModeEnabled;
        }

        private static bool PromptForDevelopmentModePassword()
        {
            var passwordBox = new System.Windows.Controls.PasswordBox
            {
                Margin = new System.Windows.Thickness(0, 8, 0, 8),
                MinWidth = 320
            };

            var validationMessage = new System.Windows.Controls.TextBlock
            {
                Text = "The development password is incorrect. Try again or " +
                       "choose Do not enable development mode.",
                Foreground = System.Windows.Media.Brushes.Red,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 420,
                Visibility = System.Windows.Visibility.Collapsed
            };

            var enableButton = new System.Windows.Controls.Button
            {
                Content = "Enable Development Mode",
                IsDefault = true,
                MinWidth = 150,
                Height = 32,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Do not enable development mode",
                IsCancel = true,
                MinWidth = 190,
                Height = 32
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            buttonPanel.Children.Add(enableButton);
            buttonPanel.Children.Add(cancelButton);

            var content = new System.Windows.Controls.StackPanel
            {
                Margin = new System.Windows.Thickness(20)
            };
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Enter the development password to enable development " +
                       "access for this Revit session.",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth = 420
            });
            content.Children.Add(passwordBox);
            content.Children.Add(validationMessage);
            content.Children.Add(buttonPanel);

            var dialog = new System.Windows.Window
            {
                Title = "ParallelSystems Development Mode",
                Content = content,
                SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                WindowStartupLocation =
                    System.Windows.WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Topmost = true,
                Icon = AppDialog.LoadWindowIcon()
            };

            bool accepted = false;
            enableButton.Click += (sender, args) =>
            {
                accepted = string.Equals(
                    passwordBox.Password,
                    DevelopmentModePassword,
                    StringComparison.Ordinal);

                if (!accepted)
                {
                    validationMessage.Visibility =
                        System.Windows.Visibility.Visible;
                    passwordBox.Clear();
                    dialog.Topmost = true;
                    dialog.Activate();
                    passwordBox.Focus();
                    return;
                }

                dialog.DialogResult = true;
                dialog.Close();
            };
            cancelButton.Click += (sender, args) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };
            dialog.Loaded += (sender, args) => passwordBox.Focus();

            try
            {
                dialog.ShowDialog();
                return accepted;
            }
            catch
            {
                return false;
            }
        }

        private void EnableDevelopmentServerBypass()
        {
            _authorizationChecked = true;
            _authorizationStarted = false;
            _authorizationUnavailableDialogShown = false;
            _isAuthorized = true;
            _servicesStarted = true;
            _timesheetTracker = null;

            IsUserAuthorized = true;
            IsAuthorizationPending = false;
            AuthorizationStatusMessage =
                "Development mode: server authorization is bypassed.";

            WriteAuthorizationLog(
                "Development server bypass enabled. " +
                "Authorization and timesheet server startup were skipped.");
        }

        public Result OnStartup(UIControlledApplication app)
        {
            StartupSplashWindow splash = null;

            try
            {
                splash = new StartupSplashWindow();
                splash.Show();
                splash.SetStatus("Loading configuration...");
                IsUserAuthorized = false;
                IsAuthorizationPending = true;
                AuthorizationStatusMessage =
                    "Authorization is being checked.";

                _authorizationStarted = false;
                _authorizationChecked = false;
                _authorizationUnavailableDialogShown = false;
                _isAuthorized = false;
                _servicesStarted = false;
                _authorizationTask = null;
                _authorizationUsername = null;
                _nextAuthorizationAttemptUtc = DateTime.MinValue;

                _authorizationCancellation?.Dispose();
                _authorizationCancellation =
                    new CancellationTokenSource();

                Configs.RevitConfig.RevitYear =
                    app.ControlledApplication.VersionNumber;

                ApiConfig.ApiSettings =
                    Helpers.Config.LoadApiSettings();

                Helpers.Config.Load(true);

                if (IsDevelopmentServerBypassEnabled())
                {
                    EnableDevelopmentServerBypass();
                }

                Helpers.BackgroundPublishRunner.Register(app);
                splash?.SetStatus("Creating the ParallelSystems ribbon...");

                /*
                 * Ribbon creation belongs in OnStartup.
                 *
                 * The ribbon will be visible before authorization finishes,
                 * so protected commands must check App.IsUserAuthorized.
                 */
                BuildRibbon(app);
                splash?.SetStatus("Registering Revit services...");

                /*
                 * Register all Revit events here.
                 *
                 * Do not add or remove event handlers while an Idling event
                 * is currently executing.
                 */
                app.ControlledApplication.DocumentOpened +=
                    ControlledApplication_DocumentOpened;

                app.ControlledApplication.DocumentClosed +=
                    ControlledApplication_DocumentClosed;

                app.ControlledApplication.DocumentChanged +=
                    ControlledApplication_DocumentChanged;

                app.ControlledApplication.DocumentSaved +=
                    ControlledApplication_DocumentSaved;

                app.ViewActivated +=
                    OnViewActivated;

                app.Idling +=
                    OnIdling;

                splash?.CompleteLoading();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                splash?.CloseSafely();

                AppDialog.Error(
                    "ParallelSystems Startup Error",
                    "The add-in failed to start.\n\n" +
                    ex.Message);

                return Result.Failed;
            }
        }

        private void OnIdling(
            object sender,
            IdlingEventArgs e)
        {
            UIApplication uiApp =
                sender as UIApplication;

            if (uiApp == null)
                return;

            /*
             * Authorization is started and completed from Idling, but the
             * HTTP work itself runs asynchronously. Revit's UI thread is no
             * longer blocked while a sleeping or slow server wakes up.
             */
            if (!_authorizationChecked)
            {
                ProcessAuthorization(uiApp);

                if (!_authorizationChecked)
                    return;
            }

            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnIdling(uiApp);
            }
            catch
            {
                // Timesheet errors must never crash Revit.
            }
        }

        private void ProcessAuthorization(
            UIApplication uiApp)
        {
            if (IsDevelopmentServerBypassEnabled())
            {
                EnableDevelopmentServerBypass();
                return;
            }

            if (_authorizationChecked)
                return;

            if (_authorizationTask == null)
            {
                if (_authorizationStarted ||
                    DateTime.UtcNow < _nextAuthorizationAttemptUtc)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_authorizationUsername))
                {
                    _authorizationUsername =
                        uiApp.Application.Username;
                }

                if (string.IsNullOrWhiteSpace(_authorizationUsername))
                {
                    DenyAccess(
                        "Revit could not determine the current username.");

                    return;
                }

                _authorizationStarted = true;
                IsAuthorizationPending = true;
                AuthorizationStatusMessage =
                    "Contacting the authorization server.";

                WriteAuthorizationLog(
                    "Starting authorization for Revit user '" +
                    _authorizationUsername +
                    "' using " +
                    GetAuthorizationBaseUrlForDisplay() +
                    ".");

                CancellationToken cancellationToken =
                    _authorizationCancellation?.Token ??
                    CancellationToken.None;

                _authorizationTask =
                    CheckUserWithRetry(
                        _authorizationUsername,
                        cancellationToken);

                return;
            }

            if (!_authorizationTask.IsCompleted)
                return;

            Task<UserCheckResponse> completedTask =
                _authorizationTask;

            _authorizationTask = null;
            _authorizationStarted = false;

            try
            {
                UserCheckResponse checkUser =
                    completedTask
                        .GetAwaiter()
                        .GetResult();

                if (checkUser == null)
                {
                    ScheduleAuthorizationRetry(
                        new JsonException(
                            "The authorization server returned an empty response."));

                    return;
                }

                if (!checkUser.Allowed)
                {
                    string displayedUsername =
                        string.IsNullOrWhiteSpace(checkUser.Username)
                            ? _authorizationUsername
                            : checkUser.Username;

                    DenyAccess(
                        $"User \"{displayedUsername}\" is not authorized.");

                    return;
                }

                _authorizationChecked = true;
                _authorizationUnavailableDialogShown = false;
                _isAuthorized = true;
                IsUserAuthorized = true;
                IsAuthorizationPending = false;
                AuthorizationStatusMessage = "Authorized.";

                WriteAuthorizationLog(
                    "Authorization succeeded for Revit user '" +
                    _authorizationUsername +
                    "'.");

                StartAuthorizedServices(uiApp);
            }
            catch (OperationCanceledException)
            {
                if (_authorizationCancellation != null &&
                    _authorizationCancellation.IsCancellationRequested)
                {
                    return;
                }

                ScheduleAuthorizationRetry(
                    new TimeoutException(
                        "The authorization request was cancelled or timed out."));
            }
            catch (AuthorizationHttpException ex)
                when (!ex.IsTransient)
            {
                DenyAccess(
                    "The authorization endpoint rejected the request.\n\n" +
                    ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                /*
                 * A missing BaseUrl is a local configuration problem. Retrying
                 * cannot repair it, so report it as a real access/setup error.
                 */
                DenyAccess(ex.Message);
            }
            catch (Exception ex)
            {
                /*
                 * Timeouts, temporary HTTP failures, a waking server, and a
                 * temporarily unavailable database must not permanently lock
                 * the plugin for the rest of the Revit session.
                 */
                ScheduleAuthorizationRetry(ex);
            }
        }

        private void ScheduleAuthorizationRetry(
            Exception exception)
        {
            _authorizationChecked = false;
            _authorizationStarted = false;
            _isAuthorized = false;
            IsUserAuthorized = false;
            IsAuthorizationPending = true;

            _nextAuthorizationAttemptUtc =
                DateTime.UtcNow.Add(AuthorizationCycleRetryDelay);

            string reason = GetExceptionMessage(exception);

            AuthorizationStatusMessage =
                "Authorization service is temporarily unavailable. " +
                "Retrying automatically.";

            WriteAuthorizationLog(
                "Authorization cycle failed. Next retry is scheduled for " +
                _nextAuthorizationAttemptUtc.ToString("O") +
                ". Reason: " +
                reason);

            if (_authorizationUnavailableDialogShown)
                return;

            _authorizationUnavailableDialogShown = true;

            AppDialog.Warn(
                "ParallelSystems - Authorization Pending",
                "The authorization server did not respond successfully, " +
                "but access has not been permanently denied.\n\n" +
                "The plugin will retry automatically after " +
                (int)AuthorizationCycleRetryDelay.TotalSeconds +
                " seconds. You do not need to restart Revit.\n\n" +
                "Configured API:\n" +
                GetAuthorizationBaseUrlForDisplay() +
                "\n\nReason:\n" +
                reason +
                "\n\nDiagnostic log:\n" +
                GetAuthorizationLogPath());
        }

        private void StartAuthorizedServices(
            UIApplication uiApp)
        {
            if (!_isAuthorized || _servicesStarted)
                return;

            try
            {
                _timesheetTracker =
                    TimesheetTracker.TryCreate();

                _servicesStarted = true;

                /*
                 * A project may already be open by the time the first Idling
                 * event runs. Initialize it immediately.
                 */
                Autodesk.Revit.DB.Document activeDocument =
                    uiApp.ActiveUIDocument?.Document;

                if (activeDocument != null)
                {
                    _timesheetTracker?.OnDocumentOpened(
                        activeDocument);
                }
            }
            catch (Exception ex)
            {
                _servicesStarted = false;

                try
                {
                    _timesheetTracker?.Dispose();
                }
                catch
                {
                }

                _timesheetTracker = null;

                AppDialog.Error(
                    "ParallelSystems",
                    "The user was authorized, but the timesheet tracker " +
                    "could not be started.\n\n" +
                    ex.Message);
            }
        }

        private void DenyAccess(
            string message)
        {
            _authorizationChecked = true;
            _authorizationStarted = false;
            _isAuthorized = false;
            _servicesStarted = false;
            IsUserAuthorized = false;
            IsAuthorizationPending = false;
            AuthorizationStatusMessage = message;

            WriteAuthorizationLog(
                "Authorization denied. " + message);

            try
            {
                _timesheetTracker?.Dispose();
            }
            catch
            {
            }

            _timesheetTracker = null;

            AppDialog.Warn(
                "ParallelSystems - Access Denied",
                message);
        }

        private static void BuildRibbon(
            UIControlledApplication app)
        {
            try
            {
                app.CreateRibbonTab(TabName);
            }
            catch
            {
                // The ribbon tab may already exist.
            }

            RibbonPanel propertyMappingPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "Property Mapping");

            PropertyMappingMenu.Build(
                propertyMappingPanel);

            RibbonPanel procurementPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "Procurement");

            ProcurementMenu.Build(
                procurementPanel);

            RibbonPanel fabricationPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "Fabrication");

            FabricationMenu.Build(
                fabricationPanel);

            RibbonPanel settingsPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "Settings");

            SettingsPanelMenu.Build(
                settingsPanel);

            RibbonPanel toolsPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "Tools");

            ToolsMenu.Build(
                toolsPanel);

            RibbonPanel aboutPanel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "About");

            AboutPanelMenu.Build(
                aboutPanel);

            if (_developmentModeEnabled)
            {
                AddDevelopmentModeIndicator(app);
            }
        }

        private static void AddDevelopmentModeIndicator(
            UIControlledApplication app)
        {
            RibbonPanel panel =
                GetOrCreatePanel(
                    app,
                    TabName,
                    "DEVELOPMENT MODE");

            string assemblyPath =
                typeof(App).Assembly.Location;
            string assemblyDirectory =
                Path.GetDirectoryName(assemblyPath);

            var buttonData = new PushButtonData(
                "PS_DevelopmentModeIndicator",
                "DEVELOPMENT MODE\nACTIVE\nNO TRACKING",
                assemblyPath,
                "ParallelSystemPlugin.Commands.ReconnectCommand")
            {
                ToolTip =
                    "Development mode is active. Server authorization and " +
                    "timesheet tracking are disabled for this Revit session."
            };

            string icon16 = Path.Combine(
                assemblyDirectory,
                "Icons",
                "ParallelSystemLogo16.ico");
            string icon32 = Path.Combine(
                assemblyDirectory,
                "Icons",
                "ParallelSystemLogo32.ico");

            if (File.Exists(icon16))
            {
                buttonData.Image =
                    new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(icon16));
            }

            if (File.Exists(icon32))
            {
                buttonData.LargeImage =
                    new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(icon32));
            }

            Autodesk.Revit.UI.PushButton button =
                panel.AddItem(buttonData) as Autodesk.Revit.UI.PushButton;

            if (button != null)
                button.Enabled = false;
        }

        private void ControlledApplication_DocumentOpened(
            object sender,
            DocumentOpenedEventArgs e)
        {
            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnDocumentOpened(
                    e.Document);
            }
            catch
            {
            }
        }

        private void ControlledApplication_DocumentClosed(
            object sender,
            DocumentClosedEventArgs e)
        {
            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnDocumentClosed();
            }
            catch
            {
            }
        }

        private void ControlledApplication_DocumentChanged(
            object sender,
            DocumentChangedEventArgs e)
        {
            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnDocumentChanged(e);
            }
            catch
            {
            }
        }

        private void ControlledApplication_DocumentSaved(
            object sender,
            DocumentSavedEventArgs e)
        {
            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnDocumentSaved(
                    e.Document);
            }
            catch
            {
            }
        }

        private void OnViewActivated(
            object sender,
            ViewActivatedEventArgs e)
        {
            if (!_isAuthorized || !_servicesStarted)
                return;

            try
            {
                _timesheetTracker?.OnViewActivated(
                    e.Document,
                    e.CurrentActiveView);
            }
            catch
            {
            }
        }

        public Result OnShutdown(
            UIControlledApplication app)
        {
            try
            {
                app.ControlledApplication.DocumentOpened -=
                    ControlledApplication_DocumentOpened;

                app.ControlledApplication.DocumentClosed -=
                    ControlledApplication_DocumentClosed;

                app.ControlledApplication.DocumentChanged -=
                    ControlledApplication_DocumentChanged;

                app.ControlledApplication.DocumentSaved -=
                    ControlledApplication_DocumentSaved;

                app.ViewActivated -=
                    OnViewActivated;

                app.Idling -=
                    OnIdling;
            }
            catch
            {
            }

            try
            {
                _timesheetTracker?.Dispose();
            }
            catch
            {
            }

            _timesheetTracker = null;

            try
            {
                _authorizationCancellation?.Cancel();
                _authorizationCancellation?.Dispose();
            }
            catch
            {
            }

            _authorizationCancellation = null;
            _authorizationTask = null;
            _authorizationUsername = null;
            _authorizationStarted = false;
            _authorizationChecked = false;
            _authorizationUnavailableDialogShown = false;
            _isAuthorized = false;
            _servicesStarted = false;
            IsUserAuthorized = false;
            IsAuthorizationPending = false;
            AuthorizationStatusMessage = null;

            return Result.Succeeded;
        }

        private static RibbonPanel GetOrCreatePanel(
            UIControlledApplication app,
            string tab,
            string panel)
        {
            try
            {
                return app.CreateRibbonPanel(
                    tab,
                    panel);
            }
            catch
            {
                foreach (RibbonPanel existingPanel
                         in app.GetRibbonPanels(tab))
                {
                    if (existingPanel.Name.Equals(
                        panel,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return existingPanel;
                    }
                }

                throw;
            }
        }

        private static async Task<UserCheckResponse> CheckUserWithRetry(
            string username,
            CancellationToken cancellationToken)
        {
            Exception lastException = null;

            for (int attempt = 1;
                 attempt <= AuthorizationAttemptsPerCycle;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    WriteAuthorizationLog(
                        "Authorization HTTP attempt " +
                        attempt +
                        " of " +
                        AuthorizationAttemptsPerCycle +
                        ".");

                    return await CheckUser(
                            username,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                    when (IsTransientAuthorizationException(ex))
                {
                    lastException = ex;

                    WriteAuthorizationLog(
                        "Authorization HTTP attempt " +
                        attempt +
                        " failed: " +
                        GetExceptionMessage(ex));

                    if (attempt >= AuthorizationAttemptsPerCycle)
                        break;

                    TimeSpan delay = TimeSpan.FromSeconds(
                        AuthorizationRetryDelay.TotalSeconds * attempt);

                    await Task.Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            throw new AuthorizationUnavailableException(
                "The authorization service did not respond after " +
                AuthorizationAttemptsPerCycle +
                " attempts.",
                lastException);
        }

        private static async Task<UserCheckResponse> CheckUser(
            string username,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(
                    ApiConfig.ApiSettings?.BaseUrl))
            {
                throw new InvalidOperationException(
                    "The API BaseUrl is missing from the configuration.");
            }

            string baseUrl =
                ApiConfig.ApiSettings.BaseUrl.Trim().TrimEnd('/');

            string encodedUsername =
                Uri.EscapeDataString(username);

            string url =
                $"{baseUrl}/api/auth/check-user/{encodedUsername}";

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
                                "Authorization failed with HTTP " +
                                (int)response.StatusCode +
                                " (" +
                                response.ReasonPhrase +
                                "). Response: " +
                                json,
                                isTransient);
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

        private static bool IsTransientAuthorizationException(
            Exception exception)
        {
            AuthorizationHttpException httpStatusException =
                exception as AuthorizationHttpException;

            if (httpStatusException != null)
                return httpStatusException.IsTransient;

            return exception is AuthorizationTimeoutException ||
                   exception is HttpRequestException ||
                   exception is JsonException;
        }

        private static string GetExceptionMessage(
            Exception exception)
        {
            if (exception == null)
                return "No additional error information was provided.";

            string message = exception.Message;

            if (exception is AuthorizationUnavailableException &&
                exception.InnerException != null &&
                !string.IsNullOrWhiteSpace(
                    exception.InnerException.Message))
            {
                message = exception.InnerException.Message;
            }

            if (string.IsNullOrWhiteSpace(message))
                message = exception.GetType().Name;

            return message;
        }

        private static string GetAuthorizationBaseUrlForDisplay()
        {
            string baseUrl =
                ApiConfig.ApiSettings?.BaseUrl;

            return string.IsNullOrWhiteSpace(baseUrl)
                ? "(not configured)"
                : baseUrl.Trim();
        }

        private static string GetAuthorizationLogPath()
        {
            string revitYear =
                string.IsNullOrWhiteSpace(Configs.RevitConfig.RevitYear)
                    ? "Unknown"
                    : Configs.RevitConfig.RevitYear;

            string folder = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "Autodesk",
                "Revit",
                "Addins",
                revitYear,
                "ParallelSystemPlugin",
                "Logs");

            return Path.Combine(
                folder,
                "Authorization.log");
        }

        private static void WriteAuthorizationLog(
            string message)
        {
            try
            {
                string path = GetAuthorizationLogPath();
                string folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(folder))
                    Directory.CreateDirectory(folder);

                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " | " +
                    message +
                    Environment.NewLine);
            }
            catch
            {
                // Authorization logging must never prevent plugin startup.
            }
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

        private sealed class AuthorizationUnavailableException :
            HttpRequestException
        {
            public AuthorizationUnavailableException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }

        private sealed class AuthorizationHttpException :
            HttpRequestException
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

        private sealed class UserCheckResponse
        {
            public string Username { get; set; }

            public bool Allowed { get; set; }
        }
    }
}
