using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {

        // Revision 2026-07-24: extends the shaped-branch bore completely
        // through the saddle/header-side face so no internal diaphragm remains.
        // Revision 2026-07-24: preserves the working SET-ON shaped-branch,
        // tap-half coupling, and flange-through-bore rules. Concentric
        // butt-weld reducers are rebuilt procedurally from their two connected
        // pipe sizes. Both 30 degree chamfers and 1 mm root faces are formed
        // directly in the coaxial loft instead of by end-plane Boolean cutters.
        private const string ApplicationId =
            "ParallelSystemsPlugin.FabricationStep";

        private const string ReadyStatusViewName =
            "PS FABRICATION STATUS - READY";

        private const string PendingStatusViewName =
            "PS FABRICATION STATUS - NOT PROCESSED";

        private const double FeetToMillimetres = 304.8;
        private const double GeometryTolerance = 1.0e-8;
        private const double DiameterTolerance = 1.0e-5;

        // Client fabrication standard:
        // - 30 degree bevel measured from the end face
        // - 1 mm root face (land) for butt-weld joints, including reducers
        // - any connection involving a flange remains plain-ended
        private const double ChamferAngleDegrees = 30.0;
        private const double ChamferRootFaceMillimetres = 1.0;
        private const double BooleanExtensionMillimetres = 0.25;
        private const double ConnectorDirectionTolerance = 0.985;

        // Maximum permitted geometric departure between the dense, exact
        // saddle samples and the compact multi-span NURBS representation.
        // This controls approximation quality only; it does not change the
        // 30 degree bevel or the 1 mm fabrication land.
        private const double SmoothBRepMaximumDeviationMillimetres = 0.10;

        // Shaped branches are currently generated as SET-ON / STUB-ON.
        // The opening in the large header pipe follows the actual inside
        // diameter of the smaller branch pipe. Weld/non-connector helper
        // elements remain transparent and are omitted from the STEP file.
        private const double ShapedBranchHoleClearanceMillimetres = 0.0;
        private const double ShapedBranchFaceSearchStepMillimetres = 0.5;
        private const double ShapedBranchMaximumFaceSearchMillimetres = 50.0;

    }
}
