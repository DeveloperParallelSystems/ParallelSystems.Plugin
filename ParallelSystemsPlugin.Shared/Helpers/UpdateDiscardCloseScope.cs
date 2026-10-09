using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ParallelSystemsPlugin.Helpers
{
    // Exists only while the updater-requested native ExitRevit command is running.
    internal sealed class UpdateDiscardCloseScope : IDisposable
    {
        private readonly UIApplication _app;
        private bool _disposed;

        internal UpdateDiscardCloseScope(UIApplication app)
        {
            _app = app;
            app.DialogBoxShowing += OnDialog;
            app.Application.DocumentSaving += OnSaving;
            app.Application.DocumentSavingAs += OnSavingAs;
            app.Application.DocumentSynchronizingWithCentral += OnSynchronizing;
        }

        internal void RelinquishUnmodifiedItems()
        {
            // API equivalent of Relinquish All Mine. It does not synchronize or save.
            // That operation alone cannot discard edits: native close-without-saving
            // below discards unsaved edits and relinquishes the remaining eligible items.
            foreach (Document document in _app.Application.Documents)
            {
                if (document.IsLinked || !document.IsWorkshared || document.IsReadOnly) continue;
                using (var options = new RelinquishOptions(true))
                using (var central = new TransactWithCentralOptions())
                    WorksharingUtils.RelinquishOwnership(document, options, central);
            }
            // Modified elements are handled by native "Close project without saving".
        }

        private void OnDialog(object sender, DialogBoxShowingEventArgs e)
        {
            var dialog = e as TaskDialogShowingEventArgs;
            int result;
            if (dialog != null && UpdateDiscardDialogPolicy.TryGetResult(dialog.DialogId, out result))
            {
                dialog.OverrideResult(result);
                return;
            }
            // Unexpected errors are shown, never automatically approved. In particular,
            // don't choose "Editable elements / Relinquish": that option saves locally.
        }

        private void OnSaving(object sender, DocumentSavingEventArgs e) { if (e.Cancellable) e.Cancel(); }
        private void OnSavingAs(object sender, DocumentSavingAsEventArgs e) { if (e.Cancellable) e.Cancel(); }
        private void OnSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e) { if (e.Cancellable) e.Cancel(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _app.DialogBoxShowing -= OnDialog;
            _app.Application.DocumentSaving -= OnSaving;
            _app.Application.DocumentSavingAs -= OnSavingAs;
            _app.Application.DocumentSynchronizingWithCentral -= OnSynchronizing;
        }
    }
}
