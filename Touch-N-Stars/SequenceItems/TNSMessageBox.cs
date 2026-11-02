using Newtonsoft.Json;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TouchNStars.SequenceItems {

    [ExportMetadata("Name", "TNS Message Box")]
    [ExportMetadata("Description", "Display a message box that can be closed remotely via TNS API")]
    [ExportMetadata("Icon", "MessageBoxSVG")]
    [ExportMetadata("Category", "Touch 'N' Stars")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TNSMessageBox : SequenceItem {
        private IWindowServiceFactory windowServiceFactory;
        private Guid? currentMessageBoxId;
        private static bool templatesLoaded = false;
        private static readonly object templateLock = new object();

        // Static constructor to register the DataTemplates
        static TNSMessageBox() {
            lock (templateLock) {
                if (!templatesLoaded) {
                    try {
                        // Load the MessageBox item template (for Sequencer UI)
                        try {
                            var itemTemplate = new ResourceDictionary {
                                Source = new Uri("pack://application:,,,/TouchNStars;component/SequenceItems/Templates/TNSMessageBoxTemplate.xaml", UriKind.Absolute)
                            };
                            Application.Current?.Resources.MergedDictionaries.Add(itemTemplate);
                        } catch (System.UriFormatException itemEx) {
                            Logger.Warning($"Failed to load TNSMessageBox item template: {itemEx.Message}");
                        }

                        // Load the MessageBox result template (for Dialog window)
                        try {
                            var resultTemplate = new ResourceDictionary {
                                Source = new Uri("pack://application:,,,/TouchNStars;component/SequenceItems/Templates/TNSMessageBoxResultTemplate.xaml", UriKind.Absolute)
                            };
                            Application.Current?.Resources.MergedDictionaries.Add(resultTemplate);
                        } catch (System.UriFormatException resultEx) {
                            Logger.Warning($"Failed to load TNSMessageBox result template: {resultEx.Message}");
                        }

                        templatesLoaded = true;
                        Logger.Debug("TNSMessageBox templates loaded successfully");
                    } catch (Exception ex) {
                        Logger.Error($"Failed to load TNSMessageBox templates: {ex}");
                    }
                }
            }
        }

        [ImportingConstructor]
        public TNSMessageBox(IWindowServiceFactory windowServiceFactory) {
            this.windowServiceFactory = windowServiceFactory;
        }

        private TNSMessageBox(TNSMessageBox cloneMe) : this(cloneMe.windowServiceFactory) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new TNSMessageBox(this) {
                Text = Text,
                CloseOnTimeout = CloseOnTimeout,
                TimeoutSeconds = TimeoutSeconds,
                ContinueOnTimeout = ContinueOnTimeout
            };
        }

        private string text = "Message from Touch 'N' Stars Sequence";
        [JsonProperty]
        public string Text {
            get => text;
            set {
                text = value;
                RaisePropertyChanged();
            }
        }

        private bool closeOnTimeout = false;
        [JsonProperty]
        public bool CloseOnTimeout {
            get => closeOnTimeout;
            set {
                closeOnTimeout = value;
                RaisePropertyChanged();
            }
        }

        private int timeoutSeconds = 60;
        [JsonProperty]
        public int TimeoutSeconds {
            get => timeoutSeconds;
            set {
                timeoutSeconds = value;
                RaisePropertyChanged();
            }
        }

        private bool continueOnTimeout = true;
        [JsonProperty]
        public bool ContinueOnTimeout {
            get => continueOnTimeout;
            set {
                continueOnTimeout = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var service = windowServiceFactory.Create();
            var msgBoxResult = new TNSMessageBoxResult(Text);

            // Whoever closes the box first decides whether the sequence goes on: the dialog's own
            // buttons, the app through the API, or the timeout. A later close doesn't change it.
            var decisionLock = new object();
            string closedBy = null;
            bool continueSequence = true;

            bool Decide(string by, bool goOn) {
                lock (decisionLock) {
                    if (closedBy != null) {
                        return false;
                    }

                    closedBy = by;
                    continueSequence = goOn;
                    return true;
                }
            }

            // Ends the timeout once the box is closed, so it neither fires late nor holds up Execute
            using var dialogClosed = CancellationTokenSource.CreateLinkedTokenSource(token);

            try {
                // The buttons set msgBoxResult.Continue before they close the window
                msgBoxResult.SetCloseAction(() => {
                    Decide("dialog", msgBoxResult.Continue);
                    service?.Close();
                });

                // Register this message box in the global registry
                var registrationId = MessageBoxRegistry.Register(
                    Text,
                    service,
                    goOn => {
                        Decide("API", goOn);
                        service?.Close();
                    }
                );

                currentMessageBoxId = registrationId;

                Logger.Info($"TNS MessageBox displayed with ID: {registrationId}");
                Logger.Info($"TNS MessageBox - Timeout enabled: {CloseOnTimeout}, Timeout: {TimeoutSeconds}s, Continue on timeout: {ContinueOnTimeout}");

                // Start timeout task if enabled
                Task timeoutTask = null;
                if (CloseOnTimeout && TimeoutSeconds > 0) {
                    Logger.Info($"TNS MessageBox - Starting timeout task for {TimeoutSeconds} seconds");
                    timeoutTask = Task.Run(async () => {
                        try {
                            await Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds), dialogClosed.Token);
                            if (Decide("timeout", ContinueOnTimeout)) {
                                Logger.Info($"TNS MessageBox - Timeout triggered after {TimeoutSeconds} seconds, Continue: {ContinueOnTimeout}");
                                service?.Close();
                            }
                        } catch (OperationCanceledException) {
                            Logger.Info($"TNS MessageBox - Timeout task cancelled");
                        }
                    });
                }

                // Register cancellation handler for user/API cancellation
                using (token.Register(() => {
                    Logger.Info($"TNS MessageBox - User/API cancellation triggered");
                    service?.Close();
                })) {
                    Logger.Info($"TNS MessageBox - Showing dialog");
                    var dialog = service.ShowDialog(msgBoxResult, "Touch 'N' Stars Message").Task;

                    // A close that came before the dialog was open (the API right after the registration, a short
                    // timeout, a cancellation) found nothing to close: the dialog stayed open, and /messagebox no
                    // longer listed it. Close it again until it is closed.
                    while (!dialog.IsCompleted) {
                        bool decided;
                        lock (decisionLock) {
                            decided = closedBy != null;
                        }

                        if (decided || token.IsCancellationRequested) {
                            _ = service?.Close();
                        }

                        await Task.WhenAny(dialog, Task.Delay(200));
                    }

                    await dialog;
                    Logger.Info($"TNS MessageBox - Dialog closed");
                }

                dialogClosed.Cancel();
                if (timeoutTask != null) {
                    try {
                        await timeoutTask;
                    } catch (Exception ex) {
                        Logger.Debug($"TNS MessageBox - Timeout task exception: {ex.Message}");
                    }
                }
            } finally {
                // Always unregister when done
                if (currentMessageBoxId.HasValue) {
                    MessageBoxRegistry.Unregister(currentMessageBoxId.Value);
                    currentMessageBoxId = null;
                }
            }

            token.ThrowIfCancellationRequested();

            string by;
            bool goOnWithSequence;
            lock (decisionLock) {
                // Closed without a decision (e.g. the window went away): as before, the sequence continues
                by = closedBy ?? "dialog";
                goOnWithSequence = closedBy != null ? continueSequence : msgBoxResult.Continue;
            }

            Logger.Info($"TNS MessageBox closed by {by} - Continue: {goOnWithSequence}");
            if (!goOnWithSequence) {
                Logger.Info($"TNS MessageBox: Stopping Sequence ({by})");
                var root = ItemUtility.GetRootContainer(this.Parent);
                root?.Interrupt();
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: TNS MessageBox, Text: {Text}";
        }
    }

    public class TNSMessageBoxResult : BaseINPC {
        private Action closeAction;

        public TNSMessageBoxResult(string message) {
            this.Message = message;
            Continue = true;
        }

        public void SetCloseAction(Action action) {
            closeAction = action;
            ContinueCommand = new RelayCommand((object o) => {
                Continue = true;
                closeAction?.Invoke();
            });
            CancelCommand = new RelayCommand((object o) => {
                Continue = false;
                closeAction?.Invoke();
            });
        }

        public string Message { get; }
        public bool Continue { get; set; }

        public ICommand ContinueCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
    }
}
