// Created by Jhay
namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal enum ProjectSetupRequestType
    {
        None,
        Refresh,
        SaveProjectInformation,
        EnableWorksharingAndCreateWorksets,
        CreateMissingWorksets,
        SaveAsCloudModel,
        EnableCloudWorksharing,
        OpenManageLinks,
        OpenCopyMonitor,
        Validate,
        CompleteSetup
    }

    internal sealed class ProjectSetupRequest
    {
        public ProjectSetupRequestType Type { get; set; }
        public ProjectSetupState State { get; set; }
    }
}
