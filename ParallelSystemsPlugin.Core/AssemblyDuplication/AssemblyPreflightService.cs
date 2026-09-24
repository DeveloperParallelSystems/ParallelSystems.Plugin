using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: aggregates all predictable name collisions before writes.
    public static class AssemblyPreflightService
    {
        public static IReadOnlyList<AssemblyPreflightIssue> ValidateNames(
            IReadOnlyCollection<ProposedAssemblyName> assemblyNames,
            AssemblyConflictIndex conflicts,
            IReadOnlyCollection<ProposedDocumentationName> documentationNames)
        {
            if (assemblyNames == null)
                throw new ArgumentNullException(nameof(assemblyNames));
            if (conflicts == null)
                throw new ArgumentNullException(nameof(conflicts));
            if (documentationNames == null)
                throw new ArgumentNullException(nameof(documentationNames));

            var issues = new List<AssemblyPreflightIssue>();

            foreach (ProposedAssemblyName proposal in assemblyNames)
            {
                if (conflicts.ContainsAssemblyName(proposal.ProposedName))
                {
                    issues.Add(new AssemblyPreflightIssue(
                        AssemblyPreflightIssueKind.AssemblyNameConflict,
                        proposal.SourceName,
                        proposal.ProposedName,
                        $"Assembly name '{proposal.ProposedName}' already exists."));
                }
            }

            foreach (IGrouping<string, ProposedAssemblyName> duplicate in assemblyNames
                         .GroupBy(x => x.ProposedName, StringComparer.OrdinalIgnoreCase)
                         .Where(x => x.Count() > 1))
            {
                issues.Add(new AssemblyPreflightIssue(
                    AssemblyPreflightIssueKind.DuplicateProposedName,
                    string.Join(", ", duplicate.Select(x => x.SourceName)),
                    duplicate.Key,
                    $"More than one selected assembly proposes '{duplicate.Key}'."));
            }

            foreach (ProposedDocumentationName proposal in documentationNames)
            {
                bool isConflict = proposal.Kind == DocumentationNameKind.SheetNumber
                    ? conflicts.ContainsSheetNumber(proposal.ProposedName)
                    : conflicts.ContainsViewName(proposal.ProposedName);

                if (!isConflict)
                    continue;

                AssemblyPreflightIssueKind issueKind =
                    proposal.Kind == DocumentationNameKind.SheetNumber
                        ? AssemblyPreflightIssueKind.SheetNumberConflict
                        : AssemblyPreflightIssueKind.ViewNameConflict;

                issues.Add(new AssemblyPreflightIssue(
                    issueKind,
                    proposal.SourceAssemblyName,
                    proposal.ProposedName,
                    $"{proposal.Kind} '{proposal.ProposedName}' already exists."));
            }

            AddDocumentationBatchDuplicates(
                issues,
                documentationNames,
                DocumentationNameKind.SheetNumber,
                AssemblyPreflightIssueKind.SheetNumberConflict);
            AddDocumentationBatchDuplicates(
                issues,
                documentationNames,
                DocumentationNameKind.ViewName,
                AssemblyPreflightIssueKind.ViewNameConflict);

            return issues;
        }

        private static void AddDocumentationBatchDuplicates(
            ICollection<AssemblyPreflightIssue> issues,
            IEnumerable<ProposedDocumentationName> documentationNames,
            DocumentationNameKind documentationKind,
            AssemblyPreflightIssueKind issueKind)
        {
            foreach (IGrouping<string, ProposedDocumentationName> duplicate in
                     documentationNames
                         .Where(x => x.Kind == documentationKind)
                         .GroupBy(x => x.ProposedName, StringComparer.OrdinalIgnoreCase)
                         .Where(x => x.Count() > 1))
            {
                issues.Add(new AssemblyPreflightIssue(
                    issueKind,
                    string.Join(", ", duplicate.Select(x => x.SourceAssemblyName)),
                    duplicate.Key,
                    $"More than one selected assembly proposes {documentationKind} " +
                    $"'{duplicate.Key}'."));
            }
        }
    }
}
