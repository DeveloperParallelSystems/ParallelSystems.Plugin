namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: classified preflight issue suitable for preview UI.
    public enum AssemblyPreflightIssueKind
    {
        AssemblyNameConflict,
        DuplicateProposedName,
        SheetNumberConflict,
        ViewNameConflict,
        AmbiguousDocumentationName
    }

    public sealed class AssemblyPreflightIssue
    {
        public AssemblyPreflightIssue(
            AssemblyPreflightIssueKind kind,
            string sourceName,
            string proposedName,
            string message)
        {
            Kind = kind;
            SourceName = sourceName;
            ProposedName = proposedName;
            Message = message;
        }

        public AssemblyPreflightIssueKind Kind { get; }

        public string SourceName { get; }

        public string ProposedName { get; }

        public string Message { get; }
    }

    public enum DocumentationNameKind
    {
        SheetNumber,
        ViewName
    }

    public sealed class ProposedDocumentationName
    {
        public ProposedDocumentationName(
            string sourceAssemblyName,
            DocumentationNameKind kind,
            string proposedName)
        {
            SourceAssemblyName = sourceAssemblyName;
            Kind = kind;
            ProposedName = proposedName;
        }

        public string SourceAssemblyName { get; }

        public DocumentationNameKind Kind { get; }

        public string ProposedName { get; }
    }
}
