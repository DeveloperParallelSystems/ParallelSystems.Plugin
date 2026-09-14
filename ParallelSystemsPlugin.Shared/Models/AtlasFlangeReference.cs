using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ParallelSystemsPlugin.Models
{
    internal sealed class AtlasFlangeReferenceRow
    {
        public string Section { get; set; }
        public string Kind { get; set; }
        public string DN { get; set; }
        public string NPS { get; set; }
        public string O { get; set; }
        public string Tf { get; set; }
        public string X { get; set; }
        public string Ah { get; set; }
        public string YSlip { get; set; }
        public string YWeldingNeck { get; set; }
        public string BSlip { get; set; }
        public string BWeldingNeck { get; set; }
        public string K { get; set; }
        public string H { get; set; }
        public string Bolts { get; set; }
        public string RfStudLength { get; set; }
        public string RfMachineLength { get; set; }
        public string A { get; set; }
        public string D { get; set; }
        public string G { get; set; }
        public string BoltThread { get; set; }

        public int NominalSizeSort
        {
            get
            {
                int value;
                return int.TryParse(
                    DN,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value)
                    ? value
                    : int.MaxValue;
            }
        }
    }

    internal static class AtlasFlangeReferenceCatalog
    {
        internal const string DataFileName = "Atlas-Flange-Dimensions.psv";
        internal const string PdfFileName = "Atlas-Steels-Flange-Reference.pdf";
        internal const string AsmeDiagramFileName = "Atlas-ASME-Flange-Diagram.png";
        internal const string As2129DiagramFileName = "Atlas-AS2129-Flange-Diagram.png";

        internal static IReadOnlyList<AtlasFlangeReferenceRow> Load()
        {
            string path = ResolveReferenceFile(DataFileName);
            if (string.IsNullOrWhiteSpace(path))
                return new List<AtlasFlangeReferenceRow>().AsReadOnly();

            List<AtlasFlangeReferenceRow> rows = new List<AtlasFlangeReferenceRow>();
            foreach (string line in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] values = line.Split('|');
                if (values.Length != 21)
                    throw new InvalidDataException(
                        "Invalid Atlas flange reference row in " + path + ".");

                rows.Add(new AtlasFlangeReferenceRow
                {
                    Section = values[0],
                    Kind = values[1],
                    DN = values[2],
                    NPS = values[3],
                    O = values[4],
                    Tf = values[5],
                    X = values[6],
                    Ah = values[7],
                    YSlip = values[8],
                    YWeldingNeck = values[9],
                    BSlip = values[10],
                    BWeldingNeck = values[11],
                    K = values[12],
                    H = values[13],
                    Bolts = values[14],
                    RfStudLength = values[15],
                    RfMachineLength = values[16],
                    A = values[17],
                    D = values[18],
                    G = values[19],
                    BoltThread = values[20]
                });
            }

            return rows.AsReadOnly();
        }

        internal static string ResolveReferenceFile(string fileName)
        {
            string assemblyDirectory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);

            string[] candidates =
            {
                string.IsNullOrWhiteSpace(assemblyDirectory)
                    ? string.Empty
                    : Path.Combine(
                        assemblyDirectory,
                        "Docs",
                        "References",
                        fileName),
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "Parallel Systems",
                    "References",
                    fileName)
            };

            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        }
    }
}
