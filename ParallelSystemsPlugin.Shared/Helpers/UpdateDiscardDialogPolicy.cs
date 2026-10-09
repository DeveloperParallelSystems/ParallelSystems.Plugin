namespace ParallelSystemsPlugin.Helpers
{
    internal static class UpdateDiscardDialogPolicy
    {
        // Exact IDs and button values verified against Revit 2025 UIFrameworkRes.
        // No message-text matching or general Yes/No overrides.
        internal static bool TryGetResult(string dialogId, out int result)
        {
            switch (dialogId)
            {
                case "TaskDialog_Save_File":
                    result = 7; return true; // No
                case "TaskDialog_Changes_Not_Saved":
                case "TaskDialog_Local_Changes_Not_Synchronized_With_Central":
                    result = 1003; return true; // Discard / close without synchronizing
                case "TaskDialog_Close_Project_Without_Saving":
                    result = 1001; return true; // Relinquish all elements and worksets
                case "TaskDialog_Single_User_Local_Changes_Not_Saved_To_Cloud":
                    result = 1002; return true; // Do not save
                default:
                    result = 0; return false;
            }
        }
    }
}
