using Autodesk.Revit.DB;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal static class AssemblyIdentityFailureHandling
    {
        public static void Configure(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new RollBackOnErrorPreprocessor());
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        private sealed class RollBackOnErrorPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                bool hasError = false;
                foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(failure);
                    else
                        hasError = true;
                }

                return hasError
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue;
            }
        }
    }
}
