using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace ParallelSystemsPlugin.Classes
{
    /// <summary>
    /// Builds and saves a portable MigraDoc PDF report using the shared report defaults.
    /// </summary>
    public sealed class PdfReportBuilder
    {
        private readonly Document _doc;
        private readonly Section _section;

        /// <summary>
        /// Initializes a new report with the configured page size and default styles.
        /// </summary>
        public PdfReportBuilder()
        {
            PdfRuntime.EnsureInitialized();
            _doc = new Document();
            DefineStyles(_doc);
            _section = _doc.AddSection();
            SetupPage(_section);
        }

        /// <summary>
        /// Gets the report section used to add report content.
        /// </summary>
        public Section Section => _section;

        /// <summary>
        /// Gets the underlying MigraDoc document.
        /// </summary>
        public Document Document => _doc;

        /// <summary>
        /// Renders the report and writes it to <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Absolute or relative output PDF path.</param>
        public void Save(string path)
        {
            var renderer = new PdfDocumentRenderer()
            {
                Document = _doc
            };

            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }

        private void SetupPage(Section section)
        {
            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
        }

        private void DefineStyles(Document doc)
        {
            doc.Styles["Normal"].Font.Name = "Arial";
            doc.Styles["Normal"].Font.Size = 10;
        }
    }

}
