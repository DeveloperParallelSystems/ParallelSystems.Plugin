using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ParallelSystemsPlugin.Compatibility
{
    /// <summary>
    /// Keeps Revit-version API differences out of commands and reports.
    /// </summary>
    internal static class RevitApiCompatibility
    {
        public static bool SupportsNativePdfExport
        {
            get
            {
#if REVIT2021
                return false;
#else
                return true;
#endif
            }
        }

        public static long GetElementIdValue(ElementId elementId)
        {
            if (elementId == null)
                return -1L;

#if REVIT2023_OR_OLDER
            return elementId.IntegerValue;
#else
            return elementId.Value;
#endif
        }

        public static ElementId CreateElementId(long value)
        {
#if REVIT2023_OR_OLDER
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Revit 2021-2023 ElementId values must fit in a 32-bit signed integer.");
            }

            return new ElementId((int)value);
#else
            return new ElementId(value);
#endif
        }



        public static bool IsInvalidElementId(ElementId elementId)
        {
            return elementId == null || GetElementIdValue(elementId) == -1L;
        }

        public static FilterRule CreateCaseInsensitiveEqualsRule(
            ElementId parameterId,
            string value)
        {
            if (parameterId == null)
                throw new ArgumentNullException(nameof(parameterId));
            if (value == null)
                throw new ArgumentNullException(nameof(value));

#if REVIT2021 || REVIT2022
            return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value, false);
#else
            return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
#endif
        }

        public static FilterStringRule CreateCaseInsensitiveStringRule(
            FilterableValueProvider valueProvider,
            FilterStringRuleEvaluator evaluator,
            string ruleString)
        {
            if (valueProvider == null)
                throw new ArgumentNullException(nameof(valueProvider));
            if (evaluator == null)
                throw new ArgumentNullException(nameof(evaluator));
            if (ruleString == null)
                throw new ArgumentNullException(nameof(ruleString));

#if REVIT2021
            return new FilterStringRule(valueProvider, evaluator, ruleString, false);
#else
            return new FilterStringRule(valueProvider, evaluator, ruleString);
#endif
        }

        public static bool ExportPdf(
            Document document,
            string outputFolder,
            IList<ElementId> viewIds)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new ArgumentException("An output folder is required.", nameof(outputFolder));
            if (viewIds == null)
                throw new ArgumentNullException(nameof(viewIds));

#if REVIT2021
            throw new NotSupportedException(
                "Native Revit PDF export is unavailable in Revit 2021. " +
                "Use CSV-only publishing or print through a configured PDF printer.");
#else
            using (var options = new PDFExportOptions { Combine = false })
            {
                return document.Export(outputFolder, viewIds, options);
            }
#endif
        }
    }
}
